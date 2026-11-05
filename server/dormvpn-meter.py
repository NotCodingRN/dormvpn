#!/usr/bin/env python3
"""DormVPN meter: runs on the VPN server every minute.

1. Pulls the user list, limits and settings from the DormVPN API (the admin panel's backend).
2. Writes the sing-box config with exactly the enabled users and restarts sing-box if that changed.
3. Counts each user's traffic with nftables, blocks users over their limit (or everyone over the cap).
4. Reports this month's usage back to the API, and optionally powers the server off when idle.

How it counts: sing-box sends each user's proxied traffic through its own `direct` outbound with
a firewall mark (1000 + user id). nftables counts bytes leaving with that mark (uploads) and bytes
coming back on those connections (downloads, which the server then sends to the client). AWS bills
both as data out, so up + down is what a user costs.

Commands (run as root):  tick (default) | apply | report
"""
import copy
import datetime
import fcntl
import json
import os
import subprocess
import sys
import time
import urllib.request

CONF = "/etc/dormvpn/dormvpn.json"           # {"api_url": ..., "server_token": ...}
BASE = "/etc/dormvpn/singbox-base.json"      # sing-box config without users
SINGBOX = "/etc/sing-box/config.json"
STATE = "/var/lib/dormvpn/state.json"
SYNC_CACHE = "/var/lib/dormvpn/sync.json"
TABLE = "dormvpn"
GB = 10 ** 9            # decimal GB: limits trip slightly earlier than AWS's binary GB
ACTIVE_BYTES = 256_000  # less than this per minute counts as idle (app health checks are a few KB)


def log(msg):
    print(msg, file=sys.stderr)


def load(path, default):
    try:
        with open(path) as f:
            return json.load(f)
    except (FileNotFoundError, ValueError):
        return default


def save(path, obj, mode=0o600):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w") as f:
        json.dump(obj, f, indent=1)
    os.chmod(tmp, mode)
    os.replace(tmp, path)


def run(args, stdin=None):
    r = subprocess.run(args, input=stdin, text=True, capture_output=True)
    if r.returncode != 0:
        raise RuntimeError("%s failed: %s" % (" ".join(args), (r.stderr or r.stdout).strip()))
    return r.stdout


def mark(user):
    return 1000 + int(user["id"])


def month_now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m")


# ---------------------------------------------------------------- API

def api(method, path, body=None):
    conf = load(CONF, {})
    req = urllib.request.Request(conf["api_url"].rstrip("/") + path, method=method,
                                 data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Authorization": "Bearer " + conf["server_token"],
                                          "Content-Type": "application/json", "User-Agent": "dormvpn-meter"})
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.loads(r.read())


def sync():
    """Latest users and settings from the API; falls back to the last good copy if it's unreachable."""
    try:
        s = api("GET", "/api/server/sync")
        save(SYNC_CACHE, s)
        return s
    except Exception as e:
        log("api unreachable, using cached users: %s" % e)
        return load(SYNC_CACHE, {"users": [], "monthly_cap_gb": 0, "idle_stop_minutes": 0, "usage": {}})


# ---------------------------------------------------------------- sing-box config

def singbox_config(users):
    cfg = copy.deepcopy(load(BASE, None))
    active = [u for u in users if u.get("enabled", True)]
    cfg["inbounds"][0]["users"] = [{"name": u["name"], "uuid": u["uuid"], "flow": "xtls-rprx-vision"} for u in active]
    cfg["outbounds"] = [{"type": "direct", "tag": "u-" + u["name"], "routing_mark": mark(u)} for u in active] + \
                       [{"type": "direct", "tag": "direct"}]
    cfg["route"] = {"rules": [{"auth_user": [u["name"]], "outbound": "u-" + u["name"]} for u in active], "final": "direct"}
    return cfg


def update_singbox(users):
    new = json.dumps(singbox_config(users), indent=1)
    try:
        with open(SINGBOX) as f:
            if f.read() == new:
                return False
    except FileNotFoundError:
        pass
    tmp = SINGBOX + ".new"
    with open(tmp, "w") as f:
        f.write(new)
    run(["chown", "root:singbox", tmp])
    os.chmod(tmp, 0o640)
    try:
        run(["/usr/local/bin/sing-box", "check", "-c", tmp])
    except RuntimeError as e:
        os.remove(tmp)
        log("new sing-box config rejected, keeping the old one: %s" % e)
        return False
    os.replace(tmp, SINGBOX)
    subprocess.run(["systemctl", "restart", "sing-box"])
    log("sing-box restarted with %d users" % len([u for u in users if u.get("enabled", True)]))
    return True


# ---------------------------------------------------------------- nftables counters

def ruleset(users):
    out = ["table inet %s {}" % TABLE, "delete table inet %s" % TABLE, "table inet %s {" % TABLE]
    for u in users:
        out += ["  counter up_%d {}" % mark(u), "  counter down_%d {}" % mark(u)]
    out += ["  set blocked { type mark; }",
            "  chain out {", "    type filter hook output priority 0; policy accept;", "    meta mark @blocked drop"]
    out += ['    meta mark %d ct mark set meta mark counter name "up_%d"' % (mark(u), mark(u)) for u in users]
    out += ["  }", "  chain in {", "    type filter hook input priority 0; policy accept;", "    ct mark @blocked drop"]
    out += ['    ct mark %d counter name "down_%d"' % (mark(u), mark(u)) for u in users]
    out += ["  }", "}"]
    return "\n".join(out) + "\n"


def table_exists():
    return subprocess.run(["nft", "list", "table", "inet", TABLE], capture_output=True).returncode == 0


def read_counters():
    data = json.loads(run(["nft", "-j", "list", "counters", "table", "inet", TABLE]))
    return {c["counter"]["name"]: c["counter"]["bytes"] for c in data.get("nftables", []) if "counter" in c}


def set_blocked(users, names):
    marks = [str(mark(u)) for u in users if u["name"] in names]
    script = "flush set inet %s blocked\n" % TABLE
    if marks:
        script += "add element inet %s blocked { %s }\n" % (TABLE, ", ".join(marks))
    run(["nft", "-f", "-"], script)


# ---------------------------------------------------------------- accounting

def new_state():
    return {"month": month_now(), "usage": {}, "last_raw": {}, "marks": None,
            "last_active": time.time(), "blocked": [], "seeded": False}


def add_counters(users, state, raw):
    """Adds counter growth since the last tick to this month's usage. Returns bytes added."""
    by_mark = {mark(u): u["name"] for u in users}
    added = 0
    for name, now in raw.items():
        last = state["last_raw"].get(name, 0)
        delta = now - last if now >= last else now  # counter was reset
        direction, _, m = name.partition("_")
        user = by_mark.get(int(m)) if m.isdigit() else None
        if user and delta > 0:
            u = state["usage"].setdefault(user, {"up": 0, "down": 0})
            u[direction] = u.get(direction, 0) + delta
            added += delta
    state["last_raw"] = raw
    return added


def apply_counters(users, state):
    if table_exists():
        add_counters(users, state, read_counters())  # keep bytes since the last tick
    run(["nft", "-f", "-"], ruleset(users))
    state["last_raw"] = {}
    state["marks"] = sorted(mark(u) for u in users)


def blocked_users(s, state):
    total = sum(u["up"] + u["down"] for u in state["usage"].values())
    cap = float(s.get("monthly_cap_gb") or 0)
    out = []
    for u in s["users"]:
        used = sum(state["usage"].get(u["name"], {}).values())
        limit = float(u.get("limit_gb") or 0)
        if not u.get("enabled", True) or (limit and used >= limit * GB) or (cap and total >= cap * GB):
            out.append(u["name"])
    return out


def report_usage(state):
    try:
        api("POST", "/api/server/usage", {"month": state["month"], "users": state["usage"], "blocked": state["blocked"]})
    except Exception as e:
        log("could not report usage: %s" % e)


def tick(state):
    s = sync()
    users = s["users"]
    update_singbox(users)
    if not table_exists() or state.get("marks") != sorted(mark(u) for u in users):
        apply_counters(users, state)

    m = month_now()
    if state["month"] != m:
        report_usage(state)  # final numbers for the month that just ended
        state["month"], state["usage"] = m, {}

    if add_counters(users, state, read_counters()) >= ACTIVE_BYTES:
        state["last_active"] = time.time()

    # a fresh server (redeploy) picks up where the old one left off this month
    if not state.get("seeded") and s.get("month") == state["month"]:
        for name, v in (s.get("usage") or {}).items():
            mine = state["usage"].setdefault(name, {"up": 0, "down": 0})
            for k in ("up", "down"):
                mine[k] = max(mine.get(k, 0), int(v.get(k, 0)))
        state["seeded"] = True

    blocked = blocked_users(s, state)
    for name in set(blocked) - set(state.get("blocked", [])):
        log("blocking %s (limit reached, cap reached or turned off)" % name)
    state["blocked"] = blocked
    set_blocked(users, blocked)
    report_usage(state)
    return s


def idle_stop(s, state):
    minutes = float(s.get("idle_stop_minutes") or 0)
    if not minutes:
        return
    with open("/proc/uptime") as f:
        boot = time.time() - float(f.read().split()[0])
    if time.time() - max(state.get("last_active", 0), boot) > minutes * 60:
        log("no traffic for %d minutes: pausing the server" % minutes)
        subprocess.run(["systemctl", "poweroff"])


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "tick"
    os.makedirs(os.path.dirname(STATE), exist_ok=True)
    lock = open(STATE + ".lock", "w")
    fcntl.flock(lock, fcntl.LOCK_EX)  # the timer and manual runs can overlap
    state = load(STATE, None) or new_state()
    for k, v in new_state().items():
        state.setdefault(k, v)
    if cmd == "tick":
        s = tick(state)
        save(STATE, state)
        idle_stop(s, state)
    elif cmd == "apply":
        apply_counters(load(SYNC_CACHE, {"users": []})["users"], state)
        save(STATE, state)
    elif cmd == "report":
        print(json.dumps(state, indent=1))
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
