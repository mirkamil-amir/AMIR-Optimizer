#!/usr/bin/env python3
"""
API latency benchmark for AMIR Optimizer (read-only requests).

Signs in, waits until the sign-in quote refresh has finished (so it does not distort timings), then calls each endpoint
once ("first") and N more times ("warm"), reporting median / p95 / max latency and response size. Uses only GET requests
after sign-in, so it changes no user data (opening the news feed may ingest due provider articles, as in normal use).

Usage:
  BENCH_EMAIL=... BENCH_PASSWORD=... python3 scripts/bench/api_bench.py --base http://localhost:8081 --runs 20 --out result.json
Credentials are read from the environment and never printed.
"""
import argparse, http.cookiejar, json, os, statistics, sys, time, urllib.error, urllib.request


def client(base):
    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))

    def call(method, path, body=None, headers=None):
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(base + path, data=data, method=method, headers={"Content-Type": "application/json", **(headers or {})})
        t = time.perf_counter()
        try:
            with opener.open(req, timeout=120) as r:
                raw = r.read()
                return r.status, raw, (time.perf_counter() - t) * 1000
        except urllib.error.HTTPError as e:
            return e.code, e.read(), (time.perf_counter() - t) * 1000

    return call


def p95(values):
    s = sorted(values)
    return s[min(len(s) - 1, int(round(0.95 * (len(s) - 1))))]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="http://localhost:8081")
    ap.add_argument("--runs", type=int, default=20)
    ap.add_argument("--out")
    ap.add_argument("--only", help="measure only endpoints whose name contains this text")
    args = ap.parse_args()
    email, password = os.environ.get("BENCH_EMAIL"), os.environ.get("BENCH_PASSWORD")
    if not email or not password:
        sys.exit("Set BENCH_EMAIL and BENCH_PASSWORD.")
    call = client(args.base)

    status, raw, _ = call("GET", "/api/v1/auth/antiforgery")
    token = json.loads(raw)["token"]
    status, _, _ = call("POST", "/api/v1/auth/login", {"email": email, "password": password}, {"X-XSRF-TOKEN": token})
    if status != 200:
        sys.exit(f"Sign-in failed with HTTP {status}.")

    # Let the sign-in quote refresh finish so it does not compete with the measurements.
    for _ in range(90):
        s, raw, _ = call("GET", "/api/v1/market-data/quote-refresh/latest")
        if s != 200 or not raw or json.loads(raw) is None or json.loads(raw).get("status") not in ("Queued", "Running"):
            break
        time.sleep(2)

    portfolios = json.loads(call("GET", "/api/v1/portfolios")[1])
    pid = next(p["id"] for p in portfolios if p.get("status", "Active") == "Active")
    runs = json.loads(call("GET", f"/api/v1/portfolios/{pid}/optimization-runs?pageSize=20")[1])["items"]
    run_id = next((r["id"] for r in runs if r["status"] == "Succeeded"), None)

    endpoints = [
        ("market-data status (every page: sidebar)", "/api/v1/market-data/status"),
        ("portfolio list (every page: context)", "/api/v1/portfolios"),
        ("portfolio overview", "/api/v1/portfolios/overview"),
        ("holdings", f"/api/v1/portfolios/{pid}/holdings"),
        ("performance", f"/api/v1/portfolios/{pid}/performance"),
        ("risk", f"/api/v1/portfolios/{pid}/risk"),
        ("portfolio runs", f"/api/v1/portfolios/{pid}/optimization-runs?pageSize=20"),
        ("optimization history", "/api/v1/optimization-runs?page=1&pageSize=25"),
        ("news feed", "/api/v1/news?page=1&pageSize=20"),
        ("news feed (macro)", "/api/v1/news?page=1&pageSize=20&category=Macroeconomic"),
        ("stocks page", "/api/v1/stocks?page=1&pageSize=50&sort=name"),
        ("markets", "/api/v1/markets"),
        ("sectors", "/api/v1/sectors"),
        ("stock detail", "/api/v1/stocks/AAPL"),
        ("stock history 1Y", "/api/v1/stocks/AAPL/history?range=1Y"),
        ("company profile", "/api/v1/stocks/AAPL/profile"),
        ("watchlist", "/api/v1/watchlist?range=1M"),
        ("fx currencies", "/api/v1/fx/currencies"),
        ("fx board", "/api/v1/fx/board?base=USD&quotes=EUR,GBP,JPY,CHF,CAD,AUD,CNY,SEK,NOK"),
        ("fx history 1Y", "/api/v1/fx/history?from=EUR&to=USD&days=365"),
        ("settings", "/api/v1/settings"),
        ("research sessions", "/api/v1/research"),
    ]
    if run_id:
        endpoints += [("optimization run", f"/api/v1/optimization-runs/{run_id}"),
                      ("reality check", f"/api/v1/optimization-runs/{run_id}/reality-check")]

    if args.only:
        endpoints = [e for e in endpoints if args.only in e[0]]
    results = []
    for name, path in endpoints:
        s, raw, first = call("GET", path)
        warm = []
        for _ in range(args.runs):
            s2, raw, ms = call("GET", path)
            warm.append(ms)
        results.append({"name": name, "path": path.replace(pid, "{pid}").replace(run_id or "-", "{run}"), "status": s,
                        "first_ms": round(first, 1), "median_ms": round(statistics.median(warm), 1), "p95_ms": round(p95(warm), 1),
                        "max_ms": round(max(warm), 1), "bytes": len(raw), "runs": args.runs})
        print(f"{name:45s} {s} first {first:8.1f} ms  median {statistics.median(warm):8.1f}  p95 {p95(warm):8.1f}  {len(raw):>9,} B", flush=True)
        if args.only:
            print("   all (ms):", " ".join(f"{t:.0f}" for t in warm), flush=True)

    if args.out:
        with open(args.out, "w") as f:
            json.dump({"base": args.base, "runs": args.runs, "at": time.strftime("%Y-%m-%dT%H:%M:%S"), "results": results}, f, indent=2)


if __name__ == "__main__":
    main()
