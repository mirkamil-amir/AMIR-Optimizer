#!/usr/bin/env python3
"""
Builds the index-constituent universe files from pinned Wikipedia revisions (CC BY-SA 4.0).

Output (committed to the repository, loaded by the API on start):
  src/PortfolioOptimizer.Infrastructure/Seed/universes/indices.csv   one row per index (metadata, source, revision)
  src/PortfolioOptimizer.Infrastructure/Seed/universes/<CODE>.csv    constituents of one index

Usage:
  python3 tools/universe-builder/build_universes.py              # rebuild from the revisions pinned below
  python3 tools/universe-builder/build_universes.py --latest     # use the current revision of every page
                                                                  # (review the diff before committing!)

Rules:
  * Canonical symbols follow Yahoo Finance conventions: US tickers without suffix (share classes with '-', e.g. BRK-B),
    Xetra '.DE', Euronext Paris '.PA', London '.L' (share-class dots become '-', e.g. BT-A.L), Tokyo '.T'.
  * 'sector' is an approximate GICS-style sector so one filter works across markets; 'industry' keeps the source's own
    classification (GICS sub-industry, Prime Standard sector, ICB sector, Nikkei industry translated from German).
  * Nothing is invented: ISINs are left empty because none of the sources lists them.
Only the Python standard library is used. If Python's TLS certificates are not installed, curl is used instead.
"""
import argparse
import csv
import datetime as dt
import html
import json
import os
import re
import subprocess
import sys
import urllib.parse
import urllib.request

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "src", "PortfolioOptimizer.Infrastructure", "Seed", "universes")
UA = "PortfolioOptimizer-universe-builder/1.0 (educational portfolio research tool)"
LICENCE = "CC BY-SA 4.0"

# Pinned revisions (update deliberately with --latest, then review and commit the generated files).
PINNED = {
    "SP500": ("en", "List of S&P 500 companies", 1379247819),
    "DAX40": ("en", "DAX", 1373844817),
    "DAX30": ("en", "DAX", 1042710912),  # 2021-09-06, last revision before the expansion to 40 members (2021-09-20)
    "CAC40": ("en", "CAC 40", 1379195393),
    "FTSE100": ("en", "FTSE 100 Index", 1377721311),
    "NIKKEI225": ("de", "Nikkei 225", 271092994),
}

INDICES = {
    "SP500": dict(name="S&P 500", country="US", currency="USD", benchmark="SPY", historical=False,
                  description="500 large US companies selected by S&P Dow Jones Indices."),
    "DAX40": dict(name="DAX 40", country="DE", currency="EUR", benchmark="^GDAXI", historical=False,
                  description="Current DAX: the 40 largest companies listed on the Frankfurt Stock Exchange (Xetra) since 20 September 2021."),
    "DAX30": dict(name="DAX 30 (historical)", country="DE", currency="EUR", benchmark="^GDAXI", historical=True,
                  description="Historical composition: the 30 DAX members as of 6 September 2021, before the index was expanded to 40. "
                              "Not the current index; useful to study the classic DAX 30 universe."),
    "CAC40": dict(name="CAC 40", country="FR", currency="EUR", benchmark="^FCHI", historical=False,
                  description="40 large companies listed on Euronext Paris."),
    "FTSE100": dict(name="FTSE 100", country="GB", currency="GBP", benchmark="^FTSE", historical=False,
                    description="100 largest companies listed on the London Stock Exchange (prices in GBP; providers quote in pence)."),
    "NIKKEI225": dict(name="Nikkei 225", country="JP", currency="JPY", benchmark="^N225", historical=False,
                      description="225 large companies on the Tokyo Stock Exchange Prime market, price-weighted."),
}

MARKET = {
    "DE": dict(suffix=".DE", exchange="XETRA", mic="XETR", currency="EUR"),
    "FR": dict(suffix=".PA", exchange="Euronext Paris", mic="XPAR", currency="EUR"),
    "GB": dict(suffix=".L", exchange="London Stock Exchange", mic="XLON", currency="GBP"),
    "JP": dict(suffix=".T", exchange="Tokyo Stock Exchange", mic="XTKS", currency="JPY"),
    "NL": dict(suffix=".AS", exchange="Euronext Amsterdam", mic="XAMS", currency="EUR"),
}

# ---------------------------------------------------------------------------------------------- sector mappings

DAX_SECTOR = {
    "automotive": "Consumer Discretionary", "financial services": "Financials", "aerospace & defence": "Industrials",
    "chemicals": "Materials", "pharmaceuticals": "Health Care", "utilities": "Utilities", "healthcare": "Health Care",
    "technology": "Information Technology", "e-commerce": "Consumer Discretionary", "apparel": "Consumer Discretionary",
    "consumer goods": "Consumer Staples", "distribution": "Industrials", "logistics": "Industrials",
    "telecommunication": "Communication Services", "telecommunications": "Communication Services",
    "mechanical engineering": "Industrials", "insurance": "Financials", "construction materials": "Materials",
    "biotech": "Health Care", "biotechnology": "Health Care", "industrials": "Industrials", "industrial": "Industrials",
    "energy technology": "Industrials", "medical equipment": "Health Care", "real estate": "Real Estate",
    "retail": "Consumer Discretionary", "software": "Information Technology", "semiconductors": "Information Technology",
    "media": "Communication Services", "transportation & logistics": "Industrials", "banks": "Financials",
    "food & beverages": "Consumer Staples", "consumer services": "Consumer Discretionary",
    # labels used by the 2021 revision (DAX 30)
    "footwear": "Consumer Discretionary", "basic materials": "Materials", "automobile": "Consumer Discretionary",
    "online food ordering": "Consumer Discretionary", "communication services": "Communication Services",
}
# Historical DAX tickers that changed since; prices are only available under the current ticker.
DAX_TICKER_CHANGES = {
    "DAI": ("MBG", "Mercedes-Benz Group (formerly Daimler)"),
    "DPW": ("DHL", "DHL Group (formerly Deutsche Post)"),
}

YAHOO_STYLE_SECTOR = {  # CAC 40 table uses Yahoo/Morningstar sector names
    "industrials": "Industrials", "financial services": "Financials", "consumer cyclical": "Consumer Discretionary",
    "consumer defensive": "Consumer Staples", "technology": "Information Technology", "healthcare": "Health Care",
    "basic materials": "Materials", "communication services": "Communication Services",
    "consumer services": "Consumer Discretionary", "utilities": "Utilities", "energy": "Energy", "real estate": "Real Estate",
}

ICB_SECTOR = {  # FTSE 100 table uses ICB sector names
    "financial services": "Financials", "mining": "Materials", "support services": "Industrials",
    "industrial support services": "Industrials", "media": "Communication Services", "banks": "Financials",
    "banking services": "Financials", "multiline utilities": "Utilities", "life insurance": "Financials",
    "insurance": "Financials", "non-life insurance": "Financials", "aerospace & defence": "Industrials",
    "pharmaceuticals & biotechnology": "Health Care", "beverages": "Consumer Staples", "food & drug retailing": "Consumer Staples",
    "telecommunications services": "Communication Services", "mobile telecommunications": "Communication Services",
    "household goods & home construction": "Consumer Discretionary", "oil & gas producers": "Energy", "tobacco": "Consumer Staples",
    "personal goods": "Consumer Staples", "health care equipment & supplies": "Health Care", "collective investments": "Financials",
    "investment trusts": "Financials", "travel & leisure": "Consumer Discretionary", "travel and leisure": "Consumer Discretionary",
    "industrial engineering": "Industrials", "general retailers": "Consumer Discretionary", "retailers": "Consumer Discretionary",
    "real estate investment trusts": "Real Estate", "real estate": "Real Estate", "food & tobacco": "Consumer Staples",
    "construction & materials": "Materials", "software & computer services": "Information Technology", "chemicals": "Materials",
    "leisure goods": "Consumer Discretionary", "electronic equipment & parts": "Information Technology",
    "homebuilding & construction supplies": "Consumer Discretionary", "general industrials": "Industrials",
    "electrical utilities & independent power producers": "Utilities", "industrial goods and services": "Industrials",
    "retail hospitality": "Consumer Discretionary", "food producers": "Consumer Staples", "gas, water & multiutilities": "Utilities",
    "industrial metals & mining": "Materials", "oil & gas": "Energy", "precious metals & mining": "Materials",
}
# Company-level corrections where an ICB sector spans several GICS sectors.
FTSE_OVERRIDES = {"RKT": "Consumer Staples", "BRBY": "Consumer Discretionary", "ULVR": "Consumer Staples", "HLMA": "Industrials"}

NIKKEI_INDUSTRY = {  # German label -> (English industry, GICS-style sector)
    "Elektronik": ("Electric machinery", "Information Technology"),
    "Chemie": ("Chemicals", "Materials"),
    "Maschinen": ("Machinery", "Industrials"),
    "Automobil": ("Automobiles", "Consumer Discretionary"),
    "Pharma": ("Pharmaceuticals", "Health Care"),
    "Bau": ("Construction", "Industrials"),
    "Bank": ("Banks", "Financials"),
    "Nahrungsmittel": ("Foods", "Consumer Staples"),
    "Eisenbahnbetreiber": ("Railways", "Industrials"),
    "Einzelhandel": ("Retail", "Consumer Discretionary"),
    "NE-Metalle": ("Non-ferrous metals", "Materials"),
    "NE-Metalle (Ferronickel)": ("Non-ferrous metals (ferronickel)", "Materials"),
    "Handelshaus": ("Trading companies", "Industrials"),
    "Versicherung": ("Insurance", "Financials"),
    "Immobilien": ("Real estate", "Real Estate"),
    "Kommunikation": ("Communications", "Communication Services"),
    "Telekommunikation": ("Telecommunications", "Communication Services"),
    "Präzisionsgeräte": ("Precision instruments", "Health Care"),
    "Dienstleistungen": ("Services", "Industrials"),
    "Logistik": ("Logistics", "Industrials"),
    "Logistik (Lagerhaltung)": ("Warehousing", "Industrials"),
    "Schifffahrt": ("Shipping", "Industrials"),
    "Strom": ("Electric power", "Utilities"),
    "Gas": ("Gas", "Utilities"),
    "Elektronik, Maschinen": ("Electronics and machinery", "Industrials"),
    "Sonstige Industrie": ("Other manufacturing", "Industrials"),
    "Automatisierungstechnik": ("Automation equipment", "Industrials"),
    "Mischkonzern": ("Conglomerate", "Industrials"),
    "Kosmetik": ("Cosmetics", "Consumer Staples"),
    "Bier": ("Beer", "Consumer Staples"),
    "Druckgewerbe": ("Printing", "Industrials"),
    "Öl und Gas": ("Oil and gas", "Energy"),
    "Öl & Kohle": ("Oil and coal", "Energy"),
    "Investment": ("Investment", "Financials"),
    "Glas": ("Glass", "Materials"),
    "Textilien": ("Textiles", "Consumer Discretionary"),
    "Papier": ("Paper", "Materials"),
    "Stahl, Maschinen": ("Steel and machinery", "Materials"),
    "Stahl": ("Steel", "Materials"),
    "Baustoffe": ("Building materials", "Materials"),
    "Baustoffe (Toiletten)": ("Building products (sanitary ware)", "Industrials"),
    "Personaldienstleistung": ("Staffing services", "Industrials"),
    "Halbleiter-Zubehör": ("Semiconductor equipment", "Information Technology"),
    "Halbleiter": ("Semiconductors", "Information Technology"),
    "Dienstleistungen (Computerspiele)": ("Video games", "Communication Services"),
    "Glas- und Elektronikhersteller": ("Glass and electronics", "Information Technology"),
    "Spielwaren- und Entertainmentkonzern": ("Toys and entertainment", "Consumer Discretionary"),
    "Videospiel- und Konsolenentwicklung": ("Video games and consoles", "Communication Services"),
    "Dienstleistungen (Software)": ("Software services", "Information Technology"),
    "Softwareentwicklung": ("Software", "Information Technology"),
    "Möbel": ("Furniture", "Consumer Discretionary"),
    "Reifenhersteller": ("Tyres", "Consumer Discretionary"),
    "Gummi": ("Rubber products", "Consumer Discretionary"),
    "Freizeit": ("Leisure", "Consumer Discretionary"),
    "Internet-Dienste": ("Internet services", "Communication Services"),
    "Dienstleistungen (Onlinedienste)": ("Online services", "Communication Services"),
    "Internet (Onlinedienste)": ("Online services", "Communication Services"),
    "Internet (Mobile Gaming)": ("Mobile gaming", "Communication Services"),
    "Elektromotoren": ("Electric motors", "Industrials"),
    "Börse": ("Stock exchange operator", "Financials"),
    "Tabak": ("Tobacco", "Consumer Staples"),
    "Finanzwesen": ("Financial services", "Financials"),
    "Finanzen": ("Financial services", "Financials"),
    "Finanzdienstleistungen (Kreditkarten)": ("Credit cards", "Financials"),
    "Fluggesellschaft": ("Airline", "Industrials"),
    "Luftfahrt": ("Air transport", "Industrials"),
    "E-Commerce": ("E-commerce", "Consumer Discretionary"),
    "Internethandel": ("Online retail", "Consumer Discretionary"),
    "Keramik und Akkumulatoren": ("Ceramics and batteries", "Industrials"),
    "Uhren": ("Watches", "Consumer Discretionary"),
    "Graphit": ("Graphite electrodes", "Industrials"),
    "Fischerei": ("Fishery", "Consumer Staples"),
}

# ------------------------------------------------------------------------------------------------- fetching


def fetch_json(url):
    request = urllib.request.Request(url, headers={"User-Agent": UA})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return json.load(response)
    except urllib.error.URLError as error:
        if "CERTIFICATE_VERIFY_FAILED" not in str(error):
            raise
        out = subprocess.run(["curl", "-sS", "--max-time", "60", "-A", UA, url], check=True, capture_output=True)
        return json.loads(out.stdout)


def api(lang, **params):
    params.update(format="json", formatversion=2)
    return fetch_json(f"https://{lang}.wikipedia.org/w/api.php?" + urllib.parse.urlencode(params))


def page_html(lang, title, revision):
    parsed = api(lang, action="parse", oldid=revision, prop="text|revid") if revision else \
        api(lang, action="parse", page=title, prop="text|revid", redirects=1)
    return parsed["parse"]["text"], parsed["parse"]["revid"]


def latest_revision(lang, title):
    data = api(lang, action="query", prop="revisions", titles=title, rvprop="ids", redirects=1)
    return data["query"]["pages"][0]["revisions"][0]["revid"]


def tables(text):
    return re.findall(r'<table class="[^"]*wikitable[^"]*"[^>]*>(.*?)</table>', text, re.S)


def clean(cell):
    cell = re.sub(r"<style.*?</style>", "", cell, flags=re.S)
    cell = re.sub(r"<sup.*?</sup>", "", cell, flags=re.S)
    cell = re.sub(r"<[^>]+>", "", cell)
    return re.sub(r"\s+", " ", html.unescape(cell)).strip()


def rows(table):
    result = []
    for row in re.findall(r"<tr[^>]*>(.*?)</tr>", table, re.S):
        raw = re.findall(r"<t([dh])[^>]*>(.*?)</t[dh]>", row, re.S)
        result.append(([clean(c) for _, c in raw], [c for _, c in raw], [k for k, _ in raw]))
    return result


def find_table(text, *required_headers):
    for table in tables(text):
        parsed = rows(table)
        header = [h.lower() for h in parsed[0][0]]
        if all(any(req in h for h in header) for req in required_headers):
            return header, [r for r in parsed[1:] if r[2] and r[2][0] == "d" or (r[2] and "d" in r[2])]
    raise SystemExit(f"Table with headers {required_headers} not found")


def col(header, name):
    for i, h in enumerate(header):
        if name in h:
            return i
    raise SystemExit(f"Column '{name}' missing in {header}")

# ------------------------------------------------------------------------------------------------- parsers


def sp500(text):
    header, data = find_table(text, "symbol", "security", "gics sector")
    s, n, sec, sub = col(header, "symbol"), col(header, "security"), col(header, "gics sector"), col(header, "sub-industry")
    out = []
    for cells, raw, _ in data:
        link = raw[s].lower()
        exchange, mic = ("NASDAQ", "XNAS") if "nasdaq" in link else ("Cboe", "BATS") if "cboe" in link else ("NYSE", "XNYS")
        # Share classes use Yahoo's dash form (BRK-B), the catalogue's canonical convention.
        out.append(dict(symbol=cells[s].upper().replace(".", "-"), name=cells[n], country="US", exchange=exchange, mic=mic, currency="USD",
                        sector=cells[sec], industry=cells[sub]))
    return out


def dax(text):
    header, data = find_table(text, "company", "prime standard sector")
    t = next(i for i, h in enumerate(header) if h.startswith("ticker"))
    c, sec = col(header, "company"), col(header, "prime standard sector")
    out = []
    for cells, _, _ in data:
        # All DAX members trade on Xetra; the table sometimes shows a foreign primary listing (e.g. AIR.PA for Airbus).
        ticker = cells[t].upper().split(".")[0].split(" ")[0]
        name = cells[c]
        if ticker in DAX_TICKER_CHANGES:
            ticker, name = DAX_TICKER_CHANGES[ticker]
        label = cells[sec]
        out.append(market_row("DE", ticker, name, DAX_SECTOR.get(label.lower(), "Unclassified"), label))
    return out


def cac40(text):
    header, data = find_table(text, "company", "gics sub-industry", "ticker")
    c, sec, sub, t = col(header, "company"), col(header, "sector"), col(header, "gics sub-industry"), col(header, "ticker")
    out = []
    for cells, _, _ in data:
        # Members listed outside Paris keep their own exchange (ArcelorMittal: Euronext Amsterdam, MT.AS).
        ticker, _, suffix = cells[t].upper().partition(".")
        country = "NL" if suffix == "AS" else "FR"
        out.append(market_row(country, ticker, cells[c], YAHOO_STYLE_SECTOR.get(cells[sec].lower(), "Unclassified"), cells[sub] or cells[sec]))
    return out


def ftse100(text):
    header, data = find_table(text, "company", "ticker", "sector")
    c, t, sec = col(header, "company"), col(header, "ticker"), col(header, "sector")
    out = []
    for cells, _, _ in data:
        ticker = cells[t].upper().rstrip(".").replace(".", "-")
        label = cells[sec]
        sector = FTSE_OVERRIDES.get(ticker) or ICB_SECTOR.get(label.lower(), "Unclassified")
        out.append(market_row("GB", ticker, cells[c], sector, label[:1].upper() + label[1:]))
    return out


def nikkei225(text):
    header, data = find_table(text, "code", "name", "branche")
    code, n, b = col(header, "code"), col(header, "name"), col(header, "branche")
    out = []
    for cells, _, _ in data:
        industry, sector = NIKKEI_INDUSTRY.get(cells[b], (cells[b], "Unclassified"))
        out.append(market_row("JP", cells[code], cells[n], sector, industry))
    return out


def market_row(country, ticker, name, sector, industry):
    m = MARKET[country]
    return dict(symbol=ticker + m["suffix"], name=name, country=country, exchange=m["exchange"], mic=m["mic"],
                currency=m["currency"], sector=sector, industry=industry)


PARSERS = {"SP500": sp500, "DAX40": dax, "DAX30": dax, "CAC40": cac40, "FTSE100": ftse100, "NIKKEI225": nikkei225}

# ------------------------------------------------------------------------------------------------- main


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--latest", action="store_true", help="use the current revision instead of the pinned one")
    parser.add_argument("--only", nargs="*", help="index codes to rebuild (default: all)")
    args = parser.parse_args()
    os.makedirs(OUT, exist_ok=True)
    today = dt.date.today().isoformat()

    index_rows = {}
    indices_path = os.path.join(OUT, "indices.csv")
    if os.path.exists(indices_path):
        with open(indices_path, newline="", encoding="utf-8") as f:
            index_rows = {r["code"]: r for r in csv.DictReader(f)}

    for code, (lang, title, pinned) in PINNED.items():
        if args.only and code not in args.only:
            continue
        revision = None if args.latest and code != "DAX30" else pinned
        text, revid = page_html(lang, title, revision)
        members, seen = [], set()
        for m in PARSERS[code](text):
            if m["symbol"] in seen:
                # Source tables occasionally list a renamed company twice; keep the first row and report it.
                print(f"warning: {code}: duplicate row for {m['symbol']} ('{m['name']}') skipped", file=sys.stderr)
                continue
            seen.add(m["symbol"])
            members.append(m)
        unclassified = [m["symbol"] for m in members if m["sector"] == "Unclassified"]
        if unclassified:
            print(f"warning: {code}: no sector mapping for {unclassified}", file=sys.stderr)

        path = os.path.join(OUT, f"{code}.csv")
        with open(path, "w", newline="", encoding="utf-8") as f:
            writer = csv.DictWriter(f, fieldnames=["symbol", "name", "country", "exchange", "mic", "currency", "sector", "industry", "isin"],
                                    lineterminator="\n")
            writer.writeheader()
            for m in sorted(members, key=lambda r: r["symbol"]):
                writer.writerow({**m, "isin": ""})
        meta = INDICES[code]
        index_rows[code] = {
            "code": code, "name": meta["name"], "country": meta["country"], "currency": meta["currency"],
            "benchmark": meta["benchmark"], "is_historical": str(meta["historical"]).lower(), "members": str(len(members)),
            "source": f"Wikipedia ({lang}) \"{title}\"", "source_url": f"https://{lang}.wikipedia.org/w/index.php?oldid={revid}",
            "revision": str(revid), "licence": LICENCE,
            "verified_on": today if revid != int(index_rows.get(code, {}).get("revision") or 0) else index_rows[code]["verified_on"],
            "description": meta["description"],
        }
        print(f"{code}: {len(members)} members from {lang}:{title} revision {revid}")

    with open(indices_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=["code", "name", "country", "currency", "benchmark", "is_historical", "members", "source",
                                               "source_url", "revision", "licence", "verified_on", "description"], lineterminator="\n")
        writer.writeheader()
        for code in INDICES:
            if code in index_rows:
                writer.writerow(index_rows[code])


if __name__ == "__main__":
    main()
