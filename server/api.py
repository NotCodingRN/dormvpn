"""DormVPN API (AWS Lambda behind a Function URL).

Serves three kinds of callers, each with its own login:
  - the admin web panel (GET /), signs in with the generated admin account
  - the Windows client app, signs in with a user account the admin created (no sign-up)
  - the VPN server's meter, authenticated with SERVER_TOKEN: pulls users, pushes usage

Direct invocations (aws lambda invoke, IAM-protected) carry {"admin_action": ...} and are used
by deploy.ps1 / the admin app to set the admin password and the server details.

Storage: one DynamoDB table, items keyed by "pk":
  settings          admin login, server details, cap / idle / wake settings, id counter
  user#<name>       id, uuid, password hash, limit_gb, enabled, ver (bumped to sign the user out)
  usage#<YYYY-MM>   {"users": {name: {"up": n, "down": n}}, "blocked": [...], "updated": ts}
"""
import base64
import datetime
import hashlib
import hmac
import json
import os
import re
import secrets
import time
import uuid as uuidlib
from decimal import Decimal

import boto3

TABLE = boto3.resource("dynamodb").Table(os.environ.get("TABLE", "dormvpn"))
EC2 = boto3.client("ec2")
TOKEN_KEY = os.environ.get("TOKEN_KEY", "").encode()
SERVER_TOKEN = os.environ.get("SERVER_TOKEN", "")
GB = 10 ** 9
PBKDF2_ITER = 120_000
NAME_RE = re.compile(r"^[a-z0-9][a-z0-9_-]{0,31}$")
ADMIN_TTL = 12 * 3600
USER_TTL = 60 * 86400
LOCK_AFTER, LOCK_SECONDS = 5, 300
PANEL = open(os.path.join(os.path.dirname(__file__), "panel.html"), encoding="utf-8").read()


class HttpError(Exception):
    def __init__(self, status, msg):
        super().__init__(msg)
        self.status, self.msg = status, msg


# ---------------------------------------------------------------- helpers

def plain(o):
    """DynamoDB Decimals -> int/float, recursively."""
    if isinstance(o, list):
        return [plain(x) for x in o]
    if isinstance(o, dict):
        return {k: plain(v) for k, v in o.items()}
    if isinstance(o, Decimal):
        return int(o) if o == int(o) else float(o)
    return o


def month_now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m")


def next_reset():
    d = datetime.datetime.now(datetime.timezone.utc)
    return (datetime.date(d.year + (d.month == 12), d.month % 12 + 1, 1)).isoformat()


def b64u(b):
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


def unb64u(s):
    return base64.urlsafe_b64decode(s + "=" * (-len(s) % 4))


def hash_pw(pw):
    salt = secrets.token_bytes(16)
    h = hashlib.pbkdf2_hmac("sha256", pw.encode(), salt, PBKDF2_ITER)
    return "pbkdf2$%d$%s$%s" % (PBKDF2_ITER, salt.hex(), h.hex())


def check_pw(pw, stored):
    try:
        _, it, salt, h = stored.split("$")
        got = hashlib.pbkdf2_hmac("sha256", pw.encode(), bytes.fromhex(salt), int(it))
        return hmac.compare_digest(got.hex(), h)
    except Exception:
        return False


def new_password():
    # readable: no 0/O/1/l/I
    alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"
    return "-".join("".join(secrets.choice(alphabet) for _ in range(4)) for _ in range(4))


def sign(role, name, ver, ttl):
    body = b64u(json.dumps({"r": role, "u": name, "v": int(ver), "e": int(time.time()) + ttl}).encode())
    return body + "." + b64u(hmac.new(TOKEN_KEY, body.encode(), hashlib.sha256).digest())


def verify(token, role):
    try:
        body, sig = token.split(".")
        want = b64u(hmac.new(TOKEN_KEY, body.encode(), hashlib.sha256).digest())
        if not hmac.compare_digest(sig, want):
            return None
        p = json.loads(unb64u(body))
        return p if p["r"] == role and p["e"] > time.time() else None
    except Exception:
        return None


def update(pk, sets, adds=None, remove=None):
    """SET/ADD fields with placeholder names (DynamoDB reserves many plain words, e.g. region, name)."""
    names, vals, parts = {}, {}, []
    for i, (k, v) in enumerate(sets.items()):
        names["#s%d" % i], vals[":s%d" % i] = k, v
    for i, (k, v) in enumerate((adds or {}).items()):
        names["#a%d" % i], vals[":a%d" % i] = k, v
    expr = []
    if sets:
        expr.append("SET " + ", ".join("#s%d = :s%d" % (i, i) for i in range(len(sets))))
    if adds:
        expr.append("ADD " + ", ".join("#a%d :a%d" % (i, i) for i in range(len(adds))))
    if remove:
        for i, k in enumerate(remove):
            names["#r%d" % i] = k
        expr.append("REMOVE " + ", ".join("#r%d" % i for i in range(len(remove))))
    kw = {"ExpressionAttributeValues": vals} if vals else {}
    return TABLE.update_item(Key={"pk": pk}, UpdateExpression=" ".join(expr), ExpressionAttributeNames=names,
                             ReturnValues="ALL_NEW", **kw)["Attributes"]


def settings():
    s = plain(TABLE.get_item(Key={"pk": "settings"}).get("Item", {}))
    s.setdefault("monthly_cap_gb", 0)
    s.setdefault("idle_stop_minutes", 0)
    s.setdefault("allow_wake", True)
    return s


def get_user(name):
    return plain(TABLE.get_item(Key={"pk": "user#" + name}).get("Item")) if NAME_RE.match(name or "") else None


def scan_all():
    items, kw = [], {}
    while True:
        r = TABLE.scan(**kw)
        items += r.get("Items", [])
        if "LastEvaluatedKey" not in r:
            return plain(items)
        kw["ExclusiveStartKey"] = r["LastEvaluatedKey"]


def usage(month=None):
    return plain(TABLE.get_item(Key={"pk": "usage#" + (month or month_now())}).get("Item", {}))


def server_state(s):
    iid = s.get("instance_id")
    if not iid:
        return "none"
    try:
        r = EC2.describe_instances(InstanceIds=[iid])
        return r["Reservations"][0]["Instances"][0]["State"]["Name"]
    except Exception:
        return "unknown"


def share_link(s, u):
    return ("vless://%s@%s:443?encryption=none&flow=xtls-rprx-vision&security=reality&sni=%s&fp=chrome&pbk=%s&sid=%s&type=tcp#DormVPN-%s"
            % (u["uuid"], s.get("ip", ""), s.get("sni", ""), s.get("public_key", ""), s.get("short_id", ""), u["name"]))


def user_view(u, use, blocked):
    mine = use.get(u["name"], {})
    return {"name": u["name"], "limit_gb": u.get("limit_gb", 0), "enabled": u.get("enabled", True),
            "up": mine.get("up", 0), "down": mine.get("down", 0), "used": mine.get("up", 0) + mine.get("down", 0),
            "blocked": u["name"] in blocked, "created": u.get("created", "")}


def login_guard(key, ok):
    """Locks an account for LOCK_SECONDS after LOCK_AFTER wrong passwords in a row."""
    item = plain(TABLE.get_item(Key={"pk": key}).get("Item", {}))
    if item.get("lock_until", 0) > time.time():
        raise HttpError(429, "Too many wrong passwords. Try again in a few minutes.")
    if ok:
        if item.get("fails"):
            TABLE.update_item(Key={"pk": key}, UpdateExpression="REMOVE fails, lock_until")
        return
    fails = item.get("fails", 0) + 1
    expr, vals = "SET fails = :f", {":f": fails}
    if fails >= LOCK_AFTER:
        expr += ", lock_until = :l"
        vals[":l"] = int(time.time()) + LOCK_SECONDS
    if item:
        TABLE.update_item(Key={"pk": key}, UpdateExpression=expr, ExpressionAttributeValues=vals)
    raise HttpError(401, "Wrong username or password.")


# ---------------------------------------------------------------- auth

def bearer(event):
    h = event.get("headers") or {}
    a = h.get("authorization", "")
    return a[7:] if a.lower().startswith("bearer ") else ""


def require_admin(event):
    p = verify(bearer(event), "admin")
    if not p or p["v"] != settings().get("admin_ver", 0):
        raise HttpError(401, "Please sign in again.")


def require_user(event):
    p = verify(bearer(event), "user")
    u = get_user(p["u"]) if p else None
    if not u or p["v"] != u.get("ver", 0) or not u.get("enabled", True):
        raise HttpError(401, "You've been signed out. Sign in again.")
    return u


def require_server(event):
    if not SERVER_TOKEN or not hmac.compare_digest(bearer(event), SERVER_TOKEN):
        raise HttpError(401, "bad server token")


# ---------------------------------------------------------------- routes: admin panel

def admin_login(body):
    s = settings()
    name, pw = body.get("username", ""), body.get("password", "")
    ok = bool(s.get("admin_hash")) and name == s.get("admin_user") and check_pw(pw, s["admin_hash"])
    login_guard("settings", ok)
    return {"token": sign("admin", name, s.get("admin_ver", 0), ADMIN_TTL)}


def admin_state():
    s = settings()
    items = scan_all()
    m = month_now()
    cur = next((i for i in items if i["pk"] == "usage#" + m), {})
    use, blocked = cur.get("users", {}), cur.get("blocked", [])
    users = sorted((user_view(i, use, blocked) for i in items if i["pk"].startswith("user#")), key=lambda u: u["name"])
    history = sorted(({"month": i["pk"][6:], "total": sum(v.get("up", 0) + v.get("down", 0) for v in i.get("users", {}).values())}
                      for i in items if i["pk"].startswith("usage#") and i["pk"] != "usage#" + m),
                     key=lambda h: h["month"], reverse=True)[:6]
    return {"month": m, "resets": next_reset(), "users": users, "history": history,
            "settings": {k: s[k] for k in ("monthly_cap_gb", "idle_stop_minutes", "allow_wake")},
            "server": {"state": server_state(s), "ip": s.get("ip", ""), "region": s.get("region", ""),
                       "instance_id": s.get("instance_id", ""), "last_report": cur.get("updated", 0)}}


def admin_add_user(body):
    name = (body.get("name") or "").strip().lower()
    if not NAME_RE.match(name):
        raise HttpError(400, "Usernames are lowercase letters, digits, - and _ (up to 32), e.g. alex")
    if get_user(name):
        raise HttpError(409, "There's already a user called %s." % name)
    limit = float(body.get("limit_gb") or 0)
    pw = body.get("password") or new_password()
    if len(pw) < 8:
        raise HttpError(400, "Passwords need at least 8 characters.")
    uid = int(update("settings", {}, {"next_id": 1})["next_id"])
    TABLE.put_item(Item={"pk": "user#" + name, "name": name, "id": uid, "uuid": str(uuidlib.uuid4()),
                         "pw": hash_pw(pw), "limit_gb": Decimal(str(limit)), "enabled": True, "ver": 0,
                         "created": datetime.date.today().isoformat()},
                   ConditionExpression="attribute_not_exists(pk)")
    return {"name": name, "password": pw}


def admin_edit_user(name, body):
    u = get_user(name)
    if not u:
        raise HttpError(404, "No such user.")
    sets, adds, out = {}, {}, {"name": name}
    if "limit_gb" in body:
        sets["limit_gb"] = Decimal(str(float(body["limit_gb"] or 0)))
    if "enabled" in body:
        sets["enabled"] = bool(body["enabled"])
        if not body["enabled"]:
            adds["ver"] = 1  # signs them out of the app
    if body.get("reset_password"):
        pw = body.get("password") or new_password()
        if len(pw) < 8:
            raise HttpError(400, "Passwords need at least 8 characters.")
        sets["pw"] = hash_pw(pw)
        adds["ver"] = 1
        out["password"] = pw
    if body.get("rotate_key"):
        sets["uuid"] = str(uuidlib.uuid4())
    if not sets and not adds:
        raise HttpError(400, "Nothing to change.")
    update("user#" + name, sets, adds)
    return out


def admin_delete_user(name):
    if not get_user(name):
        raise HttpError(404, "No such user.")
    TABLE.delete_item(Key={"pk": "user#" + name})
    return {"deleted": name}


def admin_settings(body):
    sets = {}
    if "monthly_cap_gb" in body:
        sets["monthly_cap_gb"] = Decimal(str(max(0.0, float(body["monthly_cap_gb"] or 0))))
    if "idle_stop_minutes" in body:
        m = int(body["idle_stop_minutes"] or 0)
        if 0 < m < 10:
            raise HttpError(400, "Auto-pause needs at least 10 minutes (or 0 for off).")
        sets["idle_stop_minutes"] = m
    if "allow_wake" in body:
        sets["allow_wake"] = bool(body["allow_wake"])
    if sets:
        update("settings", sets)
    return admin_state()["settings"]


def admin_server(body):
    s = settings()
    iid, action = s.get("instance_id"), body.get("action")
    if not iid:
        raise HttpError(400, "No server deployed yet.")
    if action == "start":
        EC2.start_instances(InstanceIds=[iid])
    elif action == "stop":
        EC2.stop_instances(InstanceIds=[iid])
    else:
        raise HttpError(400, "action must be start or stop")
    return {"state": server_state(s)}


# ---------------------------------------------------------------- routes: client app

def user_login(body):
    name = (body.get("username") or "").strip().lower()
    u = get_user(name)
    ok = bool(u) and check_pw(body.get("password", ""), u.get("pw", ""))
    if not u:
        time.sleep(0.3)
        raise HttpError(401, "Wrong username or password.")
    login_guard("user#" + name, ok)
    if not u.get("enabled", True):
        raise HttpError(403, "This account is turned off. Ask the person who runs the server.")
    return {"token": sign("user", name, u.get("ver", 0), USER_TTL), "name": name}


def user_me(u):
    s = settings()
    cur = usage()
    v = user_view(u, cur.get("users", {}), cur.get("blocked", []))
    v.update({"month": month_now(), "resets": next_reset(), "allow_wake": s["allow_wake"],
              "server": {"state": server_state(s)},
              "vpn": {"server": s.get("ip", ""), "port": 443, "uuid": u["uuid"], "public_key": s.get("public_key", ""),
                      "short_id": s.get("short_id", ""), "sni": s.get("sni", "")}})
    return v


def user_wake(u):
    s = settings()
    if not s["allow_wake"]:
        raise HttpError(403, "Only the admin can start the server.")
    if u["name"] in usage().get("blocked", []):
        raise HttpError(403, "You've used your data for this month.")
    st = server_state(s)
    if st == "stopped":
        EC2.start_instances(InstanceIds=[s["instance_id"]])
        st = "pending"
    return {"state": st}


# ---------------------------------------------------------------- routes: VPN server meter

def server_sync():
    s = settings()
    users = [{"name": i["name"], "id": i["id"], "uuid": i["uuid"], "limit_gb": i.get("limit_gb", 0),
              "enabled": i.get("enabled", True)} for i in scan_all() if i["pk"].startswith("user#")]
    cur = usage()
    return {"users": sorted(users, key=lambda u: u["id"]), "monthly_cap_gb": s["monthly_cap_gb"],
            "idle_stop_minutes": s["idle_stop_minutes"], "month": month_now(), "usage": cur.get("users", {})}


def server_usage(body):
    month = body.get("month", "")
    if not re.match(r"^\d{4}-\d{2}$", month):
        raise HttpError(400, "bad month")
    users = {k: {"up": int(v.get("up", 0)), "down": int(v.get("down", 0))}
             for k, v in (body.get("users") or {}).items() if NAME_RE.match(k)}
    TABLE.put_item(Item={"pk": "usage#" + month, "users": users, "blocked": list(body.get("blocked") or []),
                         "updated": int(time.time())})
    return {"ok": True}


# ---------------------------------------------------------------- direct invoke (IAM only)

def direct(event):
    a = event["admin_action"]
    if a == "set_admin":
        TABLE.update_item(Key={"pk": "settings"},
                          UpdateExpression="SET admin_user = :u, admin_hash = :h, admin_ver = if_not_exists(admin_ver, :z) + :one REMOVE fails, lock_until",
                          ExpressionAttributeValues={":u": event["username"], ":h": hash_pw(event["password"]), ":z": 0, ":one": 1})
    elif a == "set_server":
        update("settings", {k: event[k] for k in ("instance_id", "ip", "public_key", "short_id", "sni", "region")})
    elif a == "import_user":
        name = event["name"]
        if not NAME_RE.match(name):
            return {"error": "bad name"}
        TABLE.put_item(Item={"pk": "user#" + name, "name": name, "id": int(event["id"]), "uuid": event["uuid"],
                             "pw": hash_pw(event["password"]), "limit_gb": Decimal(str(event.get("limit_gb", 0))),
                             "enabled": bool(event.get("enabled", True)), "ver": 0,
                             "created": datetime.date.today().isoformat()},
                       ConditionExpression="attribute_not_exists(pk)")
        s = settings()
        if int(s.get("next_id", 0)) < int(event["id"]):
            update("settings", {"next_id": int(event["id"])})
    elif a == "ping":
        pass
    else:
        return {"error": "unknown action"}
    return {"ok": True}


# ---------------------------------------------------------------- entry point

def respond(status, body, ctype="application/json"):
    headers = {"content-type": ctype, "cache-control": "no-store", "x-content-type-options": "nosniff",
               "referrer-policy": "no-referrer", "x-frame-options": "DENY"}
    return {"statusCode": status, "headers": headers, "body": body if isinstance(body, str) else json.dumps(body)}


def handler(event, context):
    if "admin_action" in event:
        return direct(event)
    http = event.get("requestContext", {}).get("http", {})
    method, path = http.get("method", "GET"), event.get("rawPath", "/").rstrip("/") or "/"
    try:
        raw = event.get("body") or ""
        if event.get("isBase64Encoded"):
            raw = base64.b64decode(raw).decode()
        try:
            body = json.loads(raw) if raw else {}
        except ValueError:
            raise HttpError(400, "body must be JSON")

        if method == "GET" and path == "/":
            return respond(200, PANEL, "text/html; charset=utf-8")
        if method == "POST" and path == "/api/admin/login":
            return respond(200, admin_login(body))
        if method == "POST" and path == "/api/login":
            return respond(200, user_login(body))
        if path.startswith("/api/server/"):
            require_server(event)
            if method == "GET" and path == "/api/server/sync":
                return respond(200, server_sync())
            if method == "POST" and path == "/api/server/usage":
                return respond(200, server_usage(body))
        if path.startswith("/api/admin/"):
            require_admin(event)
            m = re.match(r"^/api/admin/users/([a-z0-9_-]+)(/link)?$", path)
            if method == "GET" and path == "/api/admin/state":
                return respond(200, admin_state())
            if method == "POST" and path == "/api/admin/users":
                return respond(200, admin_add_user(body))
            if m and method == "GET" and m.group(2):
                u = get_user(m.group(1))
                if not u:
                    raise HttpError(404, "No such user.")
                return respond(200, {"link": share_link(settings(), u)})
            if m and method == "PATCH":
                return respond(200, admin_edit_user(m.group(1), body))
            if m and method == "DELETE":
                return respond(200, admin_delete_user(m.group(1)))
            if method == "POST" and path == "/api/admin/settings":
                return respond(200, admin_settings(body))
            if method == "POST" and path == "/api/admin/server":
                return respond(200, admin_server(body))
        if path in ("/api/me", "/api/wake"):
            u = require_user(event)
            if method == "GET" and path == "/api/me":
                return respond(200, user_me(u))
            if method == "POST" and path == "/api/wake":
                return respond(200, user_wake(u))
        raise HttpError(404, "not found")
    except HttpError as e:
        return respond(e.status, {"error": e.msg})
    except Exception as e:  # don't leak internals, but leave a trace in CloudWatch
        print("error:", repr(e))
        return respond(500, {"error": "Something went wrong on the server."})
