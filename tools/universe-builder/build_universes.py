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
    Xetra '.DE', Euronext Paris '.PA', London '.L' (share-class dots become '-', e.g. BT-A.L), Tokyo '.T', SIX '.SW',
    Madrid '.MC', Milan '.MI', Stockholm '.ST', Copenhagen '.CO', Toronto '.TO' (RCI-B.TO), ASX '.AX', Hong Kong '.HK'
    (four digits, 0005.HK), NSE '.NS' (M&M.NS), Singapore '.SI'.
  * Index ETFs of the newer markets (ETF<country>.csv) are a curated list in this file, not Wikipedia data.
  * 'sector' is an approximate GICS-style sector so one filter works across markets; 'industry' keeps the source's own
    classification (GICS sub-industry, Prime Standard sector, ICB sector, Nikkei industry translated from German).
  * Nothing is invented: ISINs are filled only where the source lists them (FTSE MIB). Where a source has no usable sector
    (Straits Times Index, Hang Seng "Commerce & Industry"), the sector is assigned in this file and marked as approximate.
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
    "SMI": ("en", "Swiss Market Index", 1380694390),
    "IBEX35": ("en", "IBEX 35", 1369000118),
    "FTSEMIB": ("en", "FTSE MIB", 1381720123),
    "OMXS30": ("en", "OMX Stockholm 30", 1356511453),
    "OMXC25": ("en", "OMX Copenhagen 25", 1381674834),
    "TSX60": ("en", "S&P/TSX 60", 1363917062),
    "ASX50": ("en", "S&P/ASX 50", 1368805713),
    "HSI": ("en", "Hang Seng Index", 1377881667),
    "NIFTY50": ("en", "NIFTY 50", 1379683379),
    "STI": ("en", "Straits Times Index", 1373209403),
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
    "SMI": dict(name="SMI", country="CH", currency="CHF", benchmark="^SSMI", historical=False,
                description="Swiss Market Index: the 20 largest and most liquid companies on the SIX Swiss Exchange."),
    "IBEX35": dict(name="IBEX 35", country="ES", currency="EUR", benchmark="^IBEX", historical=False,
                   description="35 most liquid companies traded on the Spanish continuous market (Bolsa de Madrid)."),
    "FTSEMIB": dict(name="FTSE MIB", country="IT", currency="EUR", benchmark="FTSEMIB.MI", historical=False,
                    description="40 most liquid and capitalised companies on Borsa Italiana (Milan)."),
    "OMXS30": dict(name="OMX Stockholm 30", country="SE", currency="SEK", benchmark="^OMX", historical=False,
                   description="30 most traded share classes on Nasdaq Stockholm."),
    "OMXC25": dict(name="OMX Copenhagen 25", country="DK", currency="DKK", benchmark="^OMXC25", historical=False,
                   description="25 most traded share classes on Nasdaq Copenhagen."),
    "TSX60": dict(name="S&P/TSX 60", country="CA", currency="CAD", benchmark="^GSPTSE", historical=False,
                  description="60 large companies on the Toronto Stock Exchange (benchmark: the broader S&P/TSX Composite)."),
    "ASX50": dict(name="S&P/ASX 50", country="AU", currency="AUD", benchmark="^AXJO", historical=False,
                  description="50 largest companies on the Australian Securities Exchange (benchmark: the broader S&P/ASX 200)."),
    "HSI": dict(name="Hang Seng Index", country="HK", currency="HKD", benchmark="^HSI", historical=False,
                description="Largest companies on the Hong Kong Stock Exchange, including mainland Chinese H-shares and red chips."),
    "NIFTY50": dict(name="NIFTY 50", country="IN", currency="INR", benchmark="^NSEI", historical=False,
                    description="50 large companies on the National Stock Exchange of India."),
    "STI": dict(name="Straits Times Index", country="SG", currency="SGD", benchmark="^STI", historical=False,
                description="30 largest companies on the Singapore Exchange."),
}

# Exchange-traded index funds of the newer markets: not on Wikipedia, so curated here. Every symbol, name, currency and
# fund type was checked against the price provider's instrument data on VERIFIED_ETFS; re-check before changing the list.
VERIFIED_ETFS = "2026-10-24"
ETFS = {
    "CH": [("CSSMI", "iShares SMI ETF (CH)", "SMI"), ("CHSPI", "iShares Core SPI ETF (CH)", "Swiss Performance Index")],
    "ES": [("LYXIB", "Amundi IBEX 35 UCITS ETF Dist", "IBEX 35"), ("BBVAI", "Acción IBEX 35 ETF", "IBEX 35")],
    "IT": [("IMIB", "iShares FTSE MIB UCITS ETF EUR (Dist)", "FTSE MIB"), ("ETFMIB", "Amundi FTSE MIB UCITS ETF Dist", "FTSE MIB")],
    "SE": [("XACT-OMXS30", "XACT OMXS30 ESG", "OMX Stockholm 30 (ESG screened)")],
    "DK": [("SPIC25KL", "Sparindex INDEX OMX C25 KL", "OMX Copenhagen 25")],
    "CA": [("XIU", "iShares S&P/TSX 60 Index ETF", "S&P/TSX 60"), ("XIC", "iShares Core S&P/TSX Capped Composite Index ETF", "S&P/TSX Capped Composite")],
    "AU": [("STW", "SPDR S&P/ASX 200 Fund", "S&P/ASX 200"), ("IOZ", "iShares Core S&P/ASX 200 ETF", "S&P/ASX 200")],
    "HK": [("2800", "Tracker Fund of Hong Kong", "Hang Seng Index"), ("2828", "Hang Seng China Enterprises Index ETF", "Hang Seng China Enterprises Index")],
    "IN": [("NIFTYBEES", "Nippon India ETF Nifty 50 BeES", "NIFTY 50"), ("BANKBEES", "Nippon India ETF Nifty Bank BeES", "NIFTY Bank")],
    "SG": [("ES3", "SPDR Straits Times Index ETF", "Straits Times Index"), ("G3B", "Nikko AM Singapore STI ETF", "Straits Times Index")],
}
ETF_COUNTRY_NAMES = {"CH": "Swiss", "ES": "Spanish", "IT": "Italian", "SE": "Swedish", "DK": "Danish", "CA": "Canadian",
                     "AU": "Australian", "HK": "Hong Kong", "IN": "Indian", "SG": "Singapore"}

MARKET = {
    "DE": dict(suffix=".DE", exchange="XETRA", mic="XETR", currency="EUR"),
    "FR": dict(suffix=".PA", exchange="Euronext Paris", mic="XPAR", currency="EUR"),
    "GB": dict(suffix=".L", exchange="London Stock Exchange", mic="XLON", currency="GBP"),
    "JP": dict(suffix=".T", exchange="Tokyo Stock Exchange", mic="XTKS", currency="JPY"),
    "NL": dict(suffix=".AS", exchange="Euronext Amsterdam", mic="XAMS", currency="EUR"),
    "CH": dict(suffix=".SW", exchange="SIX Swiss Exchange", mic="XSWX", currency="CHF"),
    "ES": dict(suffix=".MC", exchange="Bolsa de Madrid", mic="XMAD", currency="EUR"),
    "IT": dict(suffix=".MI", exchange="Borsa Italiana", mic="XMIL", currency="EUR"),
    "SE": dict(suffix=".ST", exchange="Nasdaq Stockholm", mic="XSTO", currency="SEK"),
    "DK": dict(suffix=".CO", exchange="Nasdaq Copenhagen", mic="XCSE", currency="DKK"),
    "CA": dict(suffix=".TO", exchange="Toronto Stock Exchange", mic="XTSE", currency="CAD"),
    "AU": dict(suffix=".AX", exchange="Australian Securities Exchange", mic="XASX", currency="AUD"),
    "HK": dict(suffix=".HK", exchange="Hong Kong Stock Exchange", mic="XHKG", currency="HKD"),
    "IN": dict(suffix=".NS", exchange="National Stock Exchange of India", mic="XNSE", currency="INR"),
    "SG": dict(suffix=".SI", exchange="Singapore Exchange", mic="XSES", currency="SGD"),
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

# The S&P/TSX 60 table mixes Yahoo/Morningstar and GICS sector names.
TSX_SECTOR = {**YAHOO_STYLE_SECTOR, "information technology": "Information Technology", "consumer staples": "Consumer Staples"}

SMI_SECTOR = {
    "health care": "Health Care", "healthcare": "Health Care", "retail staples": "Consumer Staples", "industrials": "Industrials",
    "financials": "Financials", "retail discretionary": "Consumer Discretionary", "materials": "Materials",
    "information technology": "Information Technology",
}

IBEX_SECTOR = {
    "construction": "Industrials", "steel": "Materials", "energy": "Utilities", "oil and gas": "Energy", "solar energy": "Utilities",
    "financial services": "Financials", "insurance": "Financials", "telecommunications": "Communication Services",
    "real estate": "Real Estate", "aviation": "Industrials", "manufacturing": "Industrials", "infrastructure": "Industrials",
    "pharmaceuticals": "Health Care", "information technology": "Information Technology", "textile": "Consumer Discretionary",
    "logistics": "Industrials", "clothes and cosmetics": "Consumer Staples", "tourism": "Consumer Discretionary",
}
IBEX_OVERRIDES = {"AMS": "Information Technology"}  # Amadeus ("Tourism" in the source) is a travel-technology company

ICB_SUPERSECTOR = {  # ICB industry/supersector names used by the FTSE MIB and OMX Copenhagen 25 tables
    "utilities": "Utilities", "health care": "Health Care", "aerospace": "Industrials", "financial services": "Financials",
    "banks": "Financials", "insurance": "Financials", "consumer products and services": "Consumer Discretionary",
    "construction and materials": "Materials", "food, beverage and tobacco": "Consumer Staples", "energy": "Energy",
    "automobiles and parts": "Consumer Discretionary", "shipbuilding": "Industrials", "telecommunications": "Communication Services",
    "industrial goods and services": "Industrials", "gambling": "Consumer Discretionary", "technology": "Information Technology",
}
FTSEMIB_OVERRIDES = {"SRG": "Utilities", "NEXI": "Financials"}  # Snam: gas network; Nexi: payments
OMXC25_OVERRIDES = {"VWS": "Industrials", "FLS": "Industrials", "ROCK-B": "Industrials"}  # wind turbines, mining equipment, insulation

NIFTY_SECTOR = {
    "metals & mining": "Materials", "services": "Industrials", "healthcare": "Health Care", "consumer durables": "Consumer Discretionary",
    "financial services": "Financials", "automobile and auto components": "Consumer Discretionary", "capital goods": "Industrials",
    "telecommunication": "Communication Services", "oil, gas & consumable fuels": "Energy", "consumer services": "Consumer Discretionary",
    "construction materials": "Materials", "information technology": "Information Technology",
    "fast moving consumer goods": "Consumer Staples", "construction": "Industrials", "power": "Utilities",
}

HSI_SUBINDEX = {"finance": "Financials", "utilities": "Utilities", "properties": "Real Estate"}
# The Hang Seng sub-index "Commerce & Industry" spans every other sector; the GICS-style sector of those members is assigned
# here from each company's main business (approximate, like the other mappings).
HSI_COMMERCE = {
    1: "Industrials", 27: "Consumer Discretionary", 66: "Industrials", 175: "Consumer Discretionary", 241: "Health Care",
    267: "Industrials", 285: "Information Technology", 288: "Consumer Staples", 291: "Consumer Staples", 300: "Consumer Discretionary",
    316: "Industrials", 322: "Consumer Staples", 386: "Energy", 669: "Industrials", 700: "Communication Services",
    762: "Communication Services", 857: "Energy", 868: "Consumer Discretionary", 881: "Consumer Discretionary", 883: "Energy",
    941: "Communication Services", 968: "Information Technology", 981: "Information Technology", 992: "Information Technology",
    1024: "Communication Services", 1044: "Consumer Staples", 1088: "Energy", 1093: "Health Care", 1099: "Health Care",
    1177: "Health Care", 1211: "Consumer Discretionary", 1378: "Materials", 1810: "Information Technology", 1876: "Consumer Staples",
    1928: "Consumer Discretionary", 1929: "Consumer Discretionary", 2015: "Consumer Discretionary", 2020: "Consumer Discretionary",
    2057: "Industrials", 2269: "Health Care", 2313: "Consumer Discretionary", 2319: "Consumer Staples", 2331: "Consumer Discretionary",
    2359: "Health Care", 2382: "Information Technology", 2618: "Industrials", 2899: "Materials", 3690: "Consumer Discretionary",
    3692: "Health Care", 6618: "Health Care", 6690: "Consumer Discretionary", 6862: "Consumer Discretionary",
    9618: "Consumer Discretionary", 9633: "Consumer Staples", 9888: "Communication Services", 9961: "Consumer Discretionary",
    9988: "Consumer Discretionary", 9992: "Consumer Discretionary", 9999: "Communication Services",
}

# The Straits Times Index table lists no sectors; assigned here from each company's main business (approximate).
STI_SECTOR = {
    "A17U": "Real Estate", "C38U": "Real Estate", "9CI": "Real Estate", "C09": "Real Estate", "D05": "Financials",
    "D01": "Consumer Staples", "J69U": "Real Estate", "BUOU": "Real Estate", "G13": "Consumer Discretionary", "H78": "Real Estate",
    "J36": "Industrials", "BN4": "Industrials", "AJBU": "Real Estate", "ME8U": "Real Estate", "M44U": "Real Estate",
    "N2IU": "Real Estate", "O39": "Financials", "S58": "Industrials", "5E2": "Industrials", "U96": "Utilities", "C6L": "Industrials",
    "S68": "Financials", "Z74": "Communication Services", "S63": "Industrials", "Y92": "Consumer Staples", "U11": "Financials",
    "U14": "Real Estate", "V03": "Information Technology", "F34": "Consumer Staples", "BS6": "Industrials",
}

# Listings whose trading currency differs from the venue's (checked against the price provider 2026-10-24): the Jardine
# group companies trade in US dollars on the Singapore Exchange. Prices are only accepted in the instrument's currency.
# Keep in sync with Markets.ForeignCurrencyListings (the simulated provider quotes them in the same currency).
CURRENCY_OVERRIDES = {"D01.SI": "USD", "H78.SI": "USD", "J36.SI": "USD"}

# Rows of a source table that are not index constituents (removed and reported).
NOT_CONSTITUENTS = {"ASX50": {"VAS": "Vanguard Australian Shares is an index fund, not an ASX 50 company"}}

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


def wiki_title(raw_cell):
    """Title of the first existing Wikipedia article linked in a table cell (red links are skipped), or ""."""
    for href in re.findall(r'<a [^>]*href="/wiki/([^"#]+)', raw_cell):
        title = urllib.parse.unquote(href).replace("_", " ")
        if ":" not in title:  # skip File:, Help:, ... namespaces
            return title
    return ""


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
                        sector=cells[sec], industry=cells[sub], wiki=wiki_title(raw[n])))
    return out


def dax(text):
    header, data = find_table(text, "company", "prime standard sector")
    t = next(i for i, h in enumerate(header) if h.startswith("ticker"))
    c, sec = col(header, "company"), col(header, "prime standard sector")
    out = []
    for cells, raw, _ in data:
        # All DAX members trade on Xetra; the table sometimes shows a foreign primary listing (e.g. AIR.PA for Airbus).
        ticker = cells[t].upper().split(".")[0].split(" ")[0]
        name = cells[c]
        if ticker in DAX_TICKER_CHANGES:
            ticker, name = DAX_TICKER_CHANGES[ticker]
        label = cells[sec]
        out.append(market_row("DE", ticker, name, DAX_SECTOR.get(label.lower(), "Unclassified"), label, wiki_title(raw[c])))
    return out


def cac40(text):
    header, data = find_table(text, "company", "gics sub-industry", "ticker")
    c, sec, sub, t = col(header, "company"), col(header, "sector"), col(header, "gics sub-industry"), col(header, "ticker")
    out = []
    for cells, raw, _ in data:
        # Members listed outside Paris keep their own exchange (ArcelorMittal: Euronext Amsterdam, MT.AS).
        ticker, _, suffix = cells[t].upper().partition(".")
        country = "NL" if suffix == "AS" else "FR"
        out.append(market_row(country, ticker, cells[c], YAHOO_STYLE_SECTOR.get(cells[sec].lower(), "Unclassified"), cells[sub] or cells[sec],
                              wiki_title(raw[c])))
    return out


def ftse100(text):
    header, data = find_table(text, "company", "ticker", "sector")
    c, t, sec = col(header, "company"), col(header, "ticker"), col(header, "sector")
    out = []
    for cells, raw, _ in data:
        ticker = cells[t].upper().rstrip(".").replace(".", "-")
        label = cells[sec]
        sector = FTSE_OVERRIDES.get(ticker) or ICB_SECTOR.get(label.lower(), "Unclassified")
        out.append(market_row("GB", ticker, cells[c], sector, label[:1].upper() + label[1:], wiki_title(raw[c])))
    return out


def nikkei225(text):
    header, data = find_table(text, "code", "name", "branche")
    code, n, b = col(header, "code"), col(header, "name"), col(header, "branche")
    out = []
    for cells, raw, _ in data:
        industry, sector = NIKKEI_INDUSTRY.get(cells[b], (cells[b], "Unclassified"))
        out.append(market_row("JP", cells[code], cells[n], sector, industry, wiki_title(raw[n])))
    return out


def name_of(cell):
    """Company name without interlanguage markers such as 'Laboratorios Rovi [es]'."""
    return re.sub(r"\s*\[[a-z]{2,3}\]$", "", cell).strip()


def smi(text):
    header, data = find_table(text, "name", "sector", "ticker")
    n, sec, t = col(header, "name"), col(header, "sector"), col(header, "ticker")
    return [market_row("CH", cells[t].upper(), name_of(cells[n]), SMI_SECTOR.get(cells[sec].lower(), "Unclassified"),
                       cells[sec][:1].upper() + cells[sec][1:], wiki_title(raw[n])) for cells, raw, _ in data]


def ibex35(text):
    header, data = find_table(text, "ticker", "company", "sector")
    t, c, sec = col(header, "ticker"), col(header, "company"), col(header, "sector")
    out = []
    for cells, raw, _ in data:
        ticker = cells[t].upper().removesuffix(".MC")
        sector = IBEX_OVERRIDES.get(ticker) or IBEX_SECTOR.get(cells[sec].lower(), "Unclassified")
        out.append(market_row("ES", ticker, name_of(cells[c]), sector, cells[sec], wiki_title(raw[c])))
    return out


def ftsemib(text):
    header, data = find_table(text, "ticker", "company", "isin", "icb")
    t, c, i, sec = col(header, "ticker"), col(header, "company"), col(header, "isin"), col(header, "icb")
    out = []
    for cells, raw, _ in data:
        ticker = cells[t].upper().removesuffix(".MI")
        sector = FTSEMIB_OVERRIDES.get(ticker) or ICB_SUPERSECTOR.get(cells[sec].lower(), "Unclassified")
        out.append(market_row("IT", ticker, name_of(cells[c]), sector, cells[sec], wiki_title(raw[c]), isin=cells[i]))
    return out


def omxs30(text):
    header, data = find_table(text, "ticker", "company", "gics")
    t, c, sec = col(header, "ticker"), col(header, "company"), col(header, "gics")
    # Yahoo-style tickers with the share class after a dash (ATCO-A.ST); the sector column is GICS already.
    return [market_row("SE", cells[t].upper().removesuffix(".ST"), name_of(cells[c]), cells[sec], cells[sec], wiki_title(raw[c]))
            for cells, raw, _ in data]


def omxc25(text):
    header, data = find_table(text, "company", "icb", "ticker")
    c, sec, t = col(header, "company"), col(header, "icb"), col(header, "ticker")
    out = []
    for cells, raw, _ in data:
        # "MAERSK A" -> MAERSK-A.CO. Nordea trades in Copenhagen as NDA-DK (NDA-SE in Stockholm, NDA-FI in Helsinki).
        ticker = cells[t].upper().replace(" ", "-")
        ticker = "NDA-DK" if ticker == "NDA" else ticker
        sector = OMXC25_OVERRIDES.get(ticker) or ICB_SUPERSECTOR.get(cells[sec].lower(), "Unclassified")
        out.append(market_row("DK", ticker, name_of(cells[c]), sector, cells[sec], wiki_title(raw[c])))
    return out


def tsx60(text):
    header, data = find_table(text, "symbol", "company", "sector")
    t, c, sec = col(header, "symbol"), col(header, "company"), col(header, "sector")
    # Share classes and units use a dash on Yahoo (RCI.B -> RCI-B.TO, BIP.UN -> BIP-UN.TO).
    return [market_row("CA", cells[t].upper().replace(".", "-"), name_of(cells[c]),
                       TSX_SECTOR.get(cells[sec].lower(), "Unclassified"), cells[sec], wiki_title(raw[c])) for cells, raw, _ in data]


def asx50(text):
    header, data = find_table(text, "symbol", "company", "sector")
    t, c, sec = col(header, "symbol"), col(header, "company"), col(header, "sector")
    return [market_row("AU", cells[t].upper(), name_of(cells[c]), cells[sec], cells[sec], wiki_title(raw[c])) for cells, raw, _ in data]


def hsi(text):
    header, data = find_table(text, "ticker", "name", "sub-index")
    t, n, sub = col(header, "ticker"), col(header, "name"), col(header, "sub-index")
    out = []
    for cells, raw, _ in data:
        code = int(re.sub(r"\D", "", cells[t]))  # "SEHK: 5" -> 0005.HK
        sector = HSI_SUBINDEX.get(cells[sub].lower()) or HSI_COMMERCE.get(code, "Unclassified")
        out.append(market_row("HK", f"{code:04d}", name_of(cells[n]), sector, f"Hang Seng sub-index: {cells[sub]}", wiki_title(raw[n])))
    return out


def nifty50(text):
    header, data = find_table(text, "company", "symbol", "sector")
    c, t, sec = col(header, "company"), col(header, "symbol"), col(header, "sector")
    return [market_row("IN", cells[t].upper(), name_of(cells[c]), NIFTY_SECTOR.get(cells[sec].lower(), "Unclassified"), cells[sec],
                       wiki_title(raw[c])) for cells, raw, _ in data]


def sti(text):
    header, data = find_table(text, "stock symbol", "company")
    t, c = col(header, "stock symbol"), col(header, "company")
    out = []
    for cells, raw, _ in data:
        ticker = cells[t].upper().replace("SGX:", "").strip()
        out.append(market_row("SG", ticker, name_of(cells[c]), STI_SECTOR.get(ticker, "Unclassified"), "", wiki_title(raw[c])))
    return out


def market_row(country, ticker, name, sector, industry, wiki="", isin=""):
    m = MARKET[country]
    symbol = ticker + m["suffix"]
    return dict(symbol=symbol, name=name, country=country, exchange=m["exchange"], mic=m["mic"],
                currency=CURRENCY_OVERRIDES.get(symbol, m["currency"]), sector=sector, industry=industry, wiki=wiki, isin=isin)


PARSERS = {"SP500": sp500, "DAX40": dax, "DAX30": dax, "CAC40": cac40, "FTSE100": ftse100, "NIKKEI225": nikkei225,
           "SMI": smi, "IBEX35": ibex35, "FTSEMIB": ftsemib, "OMXS30": omxs30, "OMXC25": omxc25, "TSX60": tsx60, "ASX50": asx50,
           "HSI": hsi, "NIFTY50": nifty50, "STI": sti}

FIELDS = ["symbol", "name", "country", "exchange", "mic", "currency", "sector", "industry", "isin", "wiki"]

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
            excluded = NOT_CONSTITUENTS.get(code, {}).get(m["symbol"].split(".")[0])
            if excluded:
                print(f"warning: {code}: {m['symbol']} skipped ({excluded})", file=sys.stderr)
                continue
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
            writer = csv.DictWriter(f, fieldnames=FIELDS, lineterminator="\n")
            writer.writeheader()
            for m in sorted(members, key=lambda r: r["symbol"]):
                writer.writerow({**m, "isin": m.get("isin", ""), "wiki": f"{lang}:{m['wiki']}" if m["wiki"] else ""})
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

    if not args.only or "ETF" in args.only:
        for country, funds in ETFS.items():
            code = f"ETF{country}"
            index = next(c for c, meta in INDICES.items() if meta["country"] == country)
            with open(os.path.join(OUT, f"{code}.csv"), "w", newline="", encoding="utf-8") as f:
                writer = csv.DictWriter(f, fieldnames=FIELDS + ["type"], lineterminator="\n")
                writer.writeheader()
                for ticker, name, tracks in sorted(funds):
                    writer.writerow({**market_row(country, ticker, name, "", f"Exchange-traded index fund ({tracks})"), "type": "Etf"})
            index_rows[code] = {
                "code": code, "name": f"{ETF_COUNTRY_NAMES[country]} index ETFs", "country": country, "currency": MARKET[country]["currency"],
                "benchmark": INDICES[index]["benchmark"], "is_historical": "false", "members": str(len(funds)),
                "source": "Curated list (tools/universe-builder)", "source_url": "", "revision": "", "licence": "",
                "verified_on": VERIFIED_ETFS,
                "description": "Exchange-traded funds that track a local index – a whole market in one instrument. Not index constituents.",
            }
            print(f"{code}: {len(funds)} funds (curated, checked {VERIFIED_ETFS})")

    with open(indices_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=["code", "name", "country", "currency", "benchmark", "is_historical", "members", "source",
                                               "source_url", "revision", "licence", "verified_on", "description"], lineterminator="\n")
        writer.writeheader()
        for code in list(INDICES) + [f"ETF{c}" for c in ETFS]:
            if code in index_rows:
                writer.writerow(index_rows[code])


if __name__ == "__main__":
    main()
