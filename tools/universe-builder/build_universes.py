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
    (four digits, 0005.HK), NSE '.NS' (M&M.NS), Singapore '.SI', and for the markets added 2026-10-25 Oslo '.OL',
    Helsinki '.HE', Brussels '.BR', Lisbon '.LS', Dublin '.IR', Vienna '.VI', Warsaw '.WA', Budapest '.BD', Athens '.AT',
    Iceland '.IC', Tel Aviv '.TA', Korea '.KS' (six digits), Shanghai '.SS', Bursa Malaysia '.KL' (four digits), Bangkok
    '.BK', B3 '.SA' (share classes separately, PETR4.SA), Jakarta '.JK', Mexico '.MX' (series letters without a space,
    GFNORTEO.MX), Johannesburg '.JO', NZX '.NZ'.
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
import time
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
    # Twenty markets added 2026-10-25 (current revisions of that day).
    "OBX": ("en", "OBX Index", 1356511451),
    "OMXH25": ("en", "OMX Helsinki 25", 1356511455),
    "BEL20": ("en", "BEL 20", 1380911122),
    "PSI": ("en", "PSI-20", 1381076092),
    "ISEQ20": ("en", "ISEQ 20", 1362357822),
    "ATX": ("en", "Austrian Traded Index", 1356511363),
    "WIG20": ("de", "WIG20", 270643862),
    "BUX": ("en", "BUX", 1356511092),
    "ATHEX25": ("en", "FTSE/Athex Large Cap", 1366728259),
    "OMXI15": ("en", "OMX Iceland 15", 1337461510),
    "TA35": ("en", "TA-35 Index", 1362844669),
    "KOSPI200": ("en", "KOSPI 200", 1379902632),
    "SSE50": ("en", "SSE 50 Index", 1356509131),
    "KLCI": ("en", "FTSE Bursa Malaysia KLCI", 1356927985),
    "SET50": ("en", "SET50 Index and SET100 Index", 1358079040),
    "IBOV": ("pt", "Lista de companhias citadas no Ibovespa", 70162555),
    "LQ45": ("en", "LQ45", 1370624740),
    "IPC": ("en", "Indice de Precios y Cotizaciones", 1364117964),
    "JSE40": ("en", "FTSE/JSE Top 40 Index", 1343081092),
    "NZX50": ("en", "S&P/NZX 50", 1381776750),
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
    "OBX": dict(name="OBX", country="NO", currency="NOK", benchmark="OSEBX.OL", historical=False,
                description="25 most traded shares on Oslo Børs (benchmark: the Oslo Børs Benchmark Index)."),
    "OMXH25": dict(name="OMX Helsinki 25", country="FI", currency="EUR", benchmark="^OMXH25", historical=False,
                   description="25 most traded share classes on Nasdaq Helsinki."),
    "BEL20": dict(name="BEL 20", country="BE", currency="EUR", benchmark="^BFX", historical=False,
                  description="20 large companies on Euronext Brussels."),
    "PSI": dict(name="PSI", country="PT", currency="EUR", benchmark="PSI20.LS", historical=False,
                description="Largest and most liquid companies on Euronext Lisbon (formerly PSI-20)."),
    "ISEQ20": dict(name="ISEQ 20", country="IE", currency="EUR", benchmark="^ISEQ", historical=False,
                   description="20 largest and most liquid companies on Euronext Dublin (benchmark: the ISEQ Overall index)."),
    "ATX": dict(name="ATX", country="AT", currency="EUR", benchmark="^ATX", historical=False,
                description="Austrian Traded Index: the 20 largest companies on the Vienna Stock Exchange."),
    "WIG20": dict(name="WIG20", country="PL", currency="PLN", benchmark="ETFBW20TR.WA", historical=False,
                  description="20 largest companies on the Warsaw Stock Exchange (benchmark: the Beta ETF WIG20TR, as the price provider has no index history)."),
    "BUX": dict(name="BUX", country="HU", currency="HUF", benchmark="", historical=False,
                description="Blue-chip index of the Budapest Stock Exchange (no benchmark: the price provider has no BUX history)."),
    "ATHEX25": dict(name="FTSE/Athex Large Cap", country="GR", currency="EUR", benchmark="GD.AT", historical=False,
                    description="25 largest companies on the Athens Stock Exchange (benchmark: the Athex Composite)."),
    "OMXI15": dict(name="OMX Iceland 15", country="IS", currency="ISK", benchmark="", historical=False,
                   description="15 most traded shares on Nasdaq Iceland (no benchmark: the price provider has no index history)."),
    "TA35": dict(name="TA-35", country="IL", currency="ILS", benchmark="TA35.TA", historical=False,
                 description="35 largest companies on the Tel Aviv Stock Exchange (prices in ILS; providers quote in agorot)."),
    "KOSPI200": dict(name="KOSPI 200", country="KR", currency="KRW", benchmark="^KS11", historical=False,
                     description="200 large companies on the Korea Exchange (benchmark: the KOSPI Composite)."),
    "SSE50": dict(name="SSE 50", country="CN", currency="CNY", benchmark="000001.SS", historical=False,
                  description="50 largest and most liquid A-shares on the Shanghai Stock Exchange (benchmark: the SSE Composite)."),
    "KLCI": dict(name="FTSE Bursa Malaysia KLCI", country="MY", currency="MYR", benchmark="^KLSE", historical=False,
                 description="30 largest companies on Bursa Malaysia."),
    "SET50": dict(name="SET50", country="TH", currency="THB", benchmark="TDEX.BK", historical=False,
                  description="50 largest and most liquid companies on the Stock Exchange of Thailand (benchmark: the ThaiDEX SET50 ETF, as the price provider has no index history)."),
    "IBOV": dict(name="Ibovespa", country="BR", currency="BRL", benchmark="^BVSP", historical=False,
                 description="Most traded shares on B3, the São Paulo stock exchange (share classes listed separately, e.g. PETR3 and PETR4)."),
    "LQ45": dict(name="LQ45", country="ID", currency="IDR", benchmark="^JKLQ45", historical=False,
                 description="45 liquid large companies on the Indonesia Stock Exchange."),
    "IPC": dict(name="S&P/BMV IPC", country="MX", currency="MXN", benchmark="^MXX", historical=False,
                description="Largest and most liquid companies on the Mexican Stock Exchange."),
    "JSE40": dict(name="FTSE/JSE Top 40", country="ZA", currency="ZAR", benchmark="^J200.JO", historical=False,
                  description="40 largest companies on the Johannesburg Stock Exchange (prices in ZAR; providers quote in cents)."),
    "NZX50": dict(name="S&P/NZX 50", country="NZ", currency="NZD", benchmark="^NZ50", historical=False,
                  description="50 largest companies on the New Zealand Exchange."),
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
    # Markets added 2026-10-25 (checked that day; the other new markets have no local index ETF with prices at the provider).
    "KR": [("069500", "KODEX 200", "KOSPI 200"), ("102110", "TIGER 200", "KOSPI 200")],
    "CN": [("510050", "China 50 ETF", "SSE 50"), ("510300", "Huatai-PineBridge CSI 300 ETF", "CSI 300")],
    "BR": [("BOVA11", "iShares Ibovespa", "Ibovespa")],
    "MX": [("NAFTRACISHRS", "iShares NAFTRAC", "S&P/BMV IPC")],
    "ZA": [("STX40", "Satrix 40 ETF", "FTSE/JSE Top 40")],
    "NZ": [("FNZ", "Smartshares NZ Top 50 ETF", "S&P/NZX 50 Portfolio"), ("TNZ", "Smartshares NZ Top 10 ETF", "S&P/NZX 10")],
    "PL": [("ETFBW20TR", "Beta ETF WIG20TR", "WIG20TR (total return)")],
    "TH": [("TDEX", "ThaiDEX SET50 ETF", "SET50")],
    "MY": [("0820EA", "FTSE Bursa Malaysia KLCI ETF", "FTSE Bursa Malaysia KLCI")],
    "ID": [("R-LQ45X", "Premier ETF LQ-45", "LQ45"), ("XIIT", "Premier ETF IDX30", "IDX30")],
}
VERIFIED_ETFS_BY_COUNTRY = {c: "2026-10-25" for c in ("KR", "CN", "BR", "MX", "ZA", "NZ", "PL", "TH", "MY", "ID")}
ETF_COUNTRY_NAMES = {"CH": "Swiss", "ES": "Spanish", "IT": "Italian", "SE": "Swedish", "DK": "Danish", "CA": "Canadian",
                     "AU": "Australian", "HK": "Hong Kong", "IN": "Indian", "SG": "Singapore", "KR": "Korean", "CN": "Chinese",
                     "BR": "Brazilian", "MX": "Mexican", "ZA": "South African", "NZ": "New Zealand", "PL": "Polish", "TH": "Thai",
                     "MY": "Malaysian", "ID": "Indonesian"}

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
    "NO": dict(suffix=".OL", exchange="Oslo Børs", mic="XOSL", currency="NOK"),
    "FI": dict(suffix=".HE", exchange="Nasdaq Helsinki", mic="XHEL", currency="EUR"),
    "BE": dict(suffix=".BR", exchange="Euronext Brussels", mic="XBRU", currency="EUR"),
    "PT": dict(suffix=".LS", exchange="Euronext Lisbon", mic="XLIS", currency="EUR"),
    "IE": dict(suffix=".IR", exchange="Euronext Dublin", mic="XDUB", currency="EUR"),
    "AT": dict(suffix=".VI", exchange="Wiener Börse", mic="XWBO", currency="EUR"),
    "PL": dict(suffix=".WA", exchange="Warsaw Stock Exchange", mic="XWAR", currency="PLN"),
    "HU": dict(suffix=".BD", exchange="Budapest Stock Exchange", mic="XBUD", currency="HUF"),
    "GR": dict(suffix=".AT", exchange="Athens Stock Exchange", mic="ASEX", currency="EUR"),
    "IS": dict(suffix=".IC", exchange="Nasdaq Iceland", mic="XICE", currency="ISK"),
    "IL": dict(suffix=".TA", exchange="Tel Aviv Stock Exchange", mic="XTAE", currency="ILS"),
    "KR": dict(suffix=".KS", exchange="Korea Exchange", mic="XKRX", currency="KRW"),
    "CN": dict(suffix=".SS", exchange="Shanghai Stock Exchange", mic="XSHG", currency="CNY"),
    "MY": dict(suffix=".KL", exchange="Bursa Malaysia", mic="XKLS", currency="MYR"),
    "TH": dict(suffix=".BK", exchange="Stock Exchange of Thailand", mic="XBKK", currency="THB"),
    "BR": dict(suffix=".SA", exchange="B3", mic="BVMF", currency="BRL"),
    "ID": dict(suffix=".JK", exchange="Indonesia Stock Exchange", mic="XIDX", currency="IDR"),
    "MX": dict(suffix=".MX", exchange="Bolsa Mexicana de Valores", mic="XMEX", currency="MXN"),
    "ZA": dict(suffix=".JO", exchange="Johannesburg Stock Exchange", mic="XJSE", currency="ZAR"),
    "NZ": dict(suffix=".NZ", exchange="New Zealand Exchange", mic="XNZE", currency="NZD"),
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

# ------------------------------------------------------------------ the twenty markets added 2026-10-25
# Sector labels of these sources mapped to the GICS-style sectors used everywhere (approximate). Where a source lists no
# sector (ISEQ 20, SSE 50, LQ45) or no tickers (ATX, WIG20, FTSE/JSE Top 40), both are assigned here from each company's
# main business and listing; every symbol was checked against the price provider (VERIFIED_NEW_MARKETS).
VERIFIED_NEW_MARKETS = "2026-10-25"

OBX_SECTOR = {
    "oil: crude producers": "Energy", "integrated oil and gas": "Energy", "oil equipment and services": "Energy",
    "offshore drilling and other services": "Energy", "marine transportation": "Industrials", "banks": "Financials",
    "full line insurance": "Financials", "life insurance": "Financials", "diversified industrials": "Industrials",
    "farming, fishing, ranching and plantations": "Consumer Staples", "food products": "Consumer Staples",
    "semiconductors": "Information Technology", "aluminum": "Materials", "fertilizers": "Materials", "airlines": "Industrials",
    "telecommunications services": "Communication Services", "machinery: industrial": "Industrials",
}
GICS_ALIASES = {  # labels close to GICS used by the BEL 20, KOSPI 200 and other tables
    "basic materials": "Materials", "electronic equipment": "Information Technology", "it": "Information Technology",
    "constructions": "Industrials", "heavy industries": "Industrials", "steels & materials": "Materials",
    "energy & chemicals": "Materials", "healthcare": "Health Care",
}
GICS_SECTORS = {"Energy", "Materials", "Industrials", "Consumer Discretionary", "Consumer Staples", "Health Care", "Financials",
                "Information Technology", "Communication Services", "Utilities", "Real Estate"}
PSI_SECTOR = {
    "basic resources": "Materials", "banks": "Financials", "industrial goods & services": "Industrials", "utilities": "Utilities",
    "energy": "Energy", "travel & leisure": "Consumer Discretionary", "personal care, drug & grocery stores": "Consumer Staples",
    "construction & materials": "Industrials", "telecommunications": "Communication Services", "technology": "Information Technology",
    "financial services": "Financials",
}
ISEQ_SECTOR = {
    "A5G": "Financials", "BIRG": "Financials", "C5H": "Consumer Discretionary", "DQ7A": "Consumer Staples", "EG7": "Financials",
    "GL9": "Consumer Staples", "GVR": "Consumer Discretionary", "GRP": "Utilities", "HMSO": "Real Estate", "IR5B": "Industrials",
    "IRES": "Real Estate", "KMR": "Materials", "KRZ": "Consumer Staples", "KRX": "Industrials", "MLC": "Health Care",
    "MIO": "Industrials", "OIZ": "Consumer Staples", "PTSB": "Financials", "RY4C": "Industrials", "UPR": "Health Care",
}
# Company (as named by the source) -> (ticker, sector): the ATX, WIG20 and FTSE/JSE Top 40 tables list no tickers.
ATX_MEMBERS = {
    "Erste Bank": ("EBS", "Financials"), "Verbund": ("VER", "Utilities"), "OMV": ("OMV", "Energy"), "BAWAG": ("BG", "Financials"),
    "Andritz": ("ANDR", "Industrials"), "Wienerberger": ("WIE", "Materials"), "Voestalpine": ("VOE", "Materials"),
    "Raiffeisen International": ("RBI", "Financials"), "Porr": ("POS", "Industrials"), "CA Immo": ("CAI", "Real Estate"),
    "EVN Group": ("EVN", "Utilities"), "AT&S": ("ATS", "Information Technology"), "Austrian Post": ("POST", "Industrials"),
    "Vienna Insurance Group": ("VIG", "Financials"), "Lenzing": ("LNZ", "Materials"), "Uniqa Insurance Group": ("UQA", "Financials"),
    "Schoeller-Bleckmann Oilfield Equipment": ("SBO", "Energy"), "Immofinanz": ("IIA", "Real Estate"), "Strabag": ("STR", "Industrials"),
    "Do & Co": ("DOC", "Consumer Discretionary"),
}
WIG20_MEMBERS = {
    "Alior Bank": ("ALR", "Financials"), "Allegro.eu": ("ALE", "Consumer Discretionary"), "Bank Pekao": ("PEO", "Financials"),
    "Budimex": ("BDX", "Industrials"), "CCC": ("CCC", "Consumer Discretionary"), "CD Projekt": ("CDR", "Communication Services"),
    "Dino Polska": ("DNP", "Consumer Staples"), "Grupa Kęty": ("KTY", "Materials"), "KGHM Polska Miedź": ("KGH", "Materials"),
    "Kruk S.A.": ("KRU", "Financials"), "LPP": ("LPP", "Consumer Discretionary"), "mBank": ("MBK", "Financials"),
    "Orange Polska": ("OPL", "Communication Services"), "Orlen": ("PKN", "Energy"), "Pepco Group": ("PCO", "Consumer Discretionary"),
    "PGE": ("PGE", "Utilities"), "PKO Bank Polski": ("PKO", "Financials"), "PZU SA": ("PZU", "Financials"),
    "Santander Bank Polska": ("SPL", "Financials"), "Żabka Group": ("ZAB", "Consumer Staples"),
}
JSE40_MEMBERS = {
    "Absa Group": ("ABG", "Financials"), "Anglo American": ("AGL", "Materials"), "Anglogold Ashanti": ("ANG", "Materials"),
    "AB InBev": ("ANH", "Consumer Staples"), "Aspen Pharmacare": ("APN", "Health Care"), "BHP": ("BHG", "Materials"),
    "Bidcorp": ("BID", "Consumer Staples"), "Bidvest Group": ("BVT", "Industrials"), "BAT": ("BTI", "Consumer Staples"),
    "Capitec": ("CPI", "Financials"), "Clicks": ("CLS", "Consumer Staples"), "Discovery": ("DSY", "Financials"),
    "Exxaro": ("EXX", "Energy"), "FirstRand": ("FSR", "Financials"), "Glencore": ("GLN", "Materials"), "Gold Fields": ("GFI", "Materials"),
    "Growthpoint Properties": ("GRT", "Real Estate"), "Implats": ("IMP", "Materials"), "Investec Ltd": ("INL", "Financials"),
    "Investec PLC": ("INP", "Financials"), "Mondi": ("MNP", "Materials"), "Mr Price Group": ("MRP", "Consumer Discretionary"),
    "MTN Group": ("MTN", "Communication Services"), "MultiChoice": ("MCG", "Communication Services"),
    "Naspers": ("NPN", "Consumer Discretionary"), "Nedbank": ("NED", "Financials"), "NEPI Rockcastle": ("NRP", "Real Estate"),
    "Northam Platinum": ("NPH", "Materials"), "Old Mutual": ("OMU", "Financials"), "Prosus": ("PRX", "Consumer Discretionary"),
    "Reinet Investments": ("RNI", "Financials"), "Remgro": ("REM", "Financials"), "RMH": ("RMH", "Financials"),
    "Sanlam": ("SLM", "Financials"), "Sasol": ("SOL", "Materials"), "Shoprite": ("SHP", "Consumer Staples"),
    "Standard Bank": ("SBK", "Financials"), "Valterra Platinum": ("VAL", "Materials"), "Vodacom": ("VOD", "Communication Services"),
    "Woolworths Holdings": ("WHL", "Consumer Discretionary"),
}
BUX_SECTOR = {
    "telecommunications": "Communication Services", "electric utilities": "Utilities", "diversified industrials": "Industrials",
    "real estate": "Real Estate", "automobiles & parts": "Consumer Discretionary", "insurance": "Financials",
    "informatics": "Information Technology", "construction": "Industrials", "oil & gas": "Energy", "industrial conglomerate": "Industrials",
    "banks": "Financials", "industrial services": "Utilities", "pharmaceuticals": "Health Care", "industrial transportation": "Industrials",
}
ATHEX_SECTOR = {
    "airlines": "Industrials", "banks": "Financials", "toys": "Consumer Discretionary", "industrial goods & services": "Industrials",
    "soft drinks": "Consumer Staples", "construction & materials": "Industrials", "basic resources": "Materials",
    "integrated oil and gas": "Energy", "water": "Utilities", "heavy construction": "Industrials",
    "fixed line telecommunications": "Communication Services", "real estate holding & development": "Real Estate",
    "exploration & production": "Energy", "nonferrous metals": "Industrials", "gambling": "Consumer Discretionary",
    "consumer products & services": "Consumer Discretionary", "conventional electricity": "Utilities", "technology": "Information Technology",
    "personal products": "Consumer Staples", "alternative electricity": "Utilities", "construction materials and fixtures": "Materials",
    "diversified industrials": "Industrials",
}
TA35_SECTOR = {
    "energy": "Utilities", "renewable energy": "Utilities", "chemicals": "Materials", "defense": "Industrials", "real estate": "Real Estate",
    "telecommunications": "Communication Services", "banking": "Financials", "oil & gas": "Energy", "insurance": "Financials",
    "semiconductors": "Information Technology", "pharma": "Health Care", "technology": "Information Technology",
    "hotels & tourism": "Consumer Discretionary", "retail": "Consumer Staples", "food processing": "Consumer Staples",
    "construction": "Industrials",
}
SSE50_SECTOR = {
    600028: "Energy", 600030: "Financials", 600031: "Industrials", 600036: "Financials", 600048: "Real Estate",
    600050: "Communication Services", 600089: "Industrials", 600104: "Consumer Discretionary", 600111: "Materials",
    600150: "Industrials", 600276: "Health Care", 600309: "Materials", 600406: "Industrials", 600436: "Health Care",
    600438: "Information Technology", 600519: "Consumer Staples", 600690: "Consumer Discretionary", 600809: "Consumer Staples",
    600887: "Consumer Staples", 600893: "Industrials", 600900: "Utilities", 600905: "Utilities", 601012: "Information Technology",
    601088: "Energy", 601166: "Financials", 601225: "Energy", 601288: "Financials", 601318: "Financials", 601390: "Industrials",
    601398: "Financials", 601601: "Financials", 601628: "Financials", 601633: "Consumer Discretionary", 601668: "Industrials",
    601669: "Industrials", 601728: "Communication Services", 601857: "Energy", 601888: "Consumer Discretionary",
    601899: "Materials", 601919: "Industrials", 601988: "Financials", 603259: "Health Care", 603288: "Consumer Staples",
    603501: "Information Technology", 603799: "Materials", 603986: "Information Technology", 688111: "Information Technology",
    688041: "Information Technology", 688599: "Information Technology", 688981: "Information Technology",
}
KLCI_SECTOR = {
    "retailers": "Consumer Staples", "retail": "Consumer Discretionary", "financial services": "Financials", "construction": "Industrials",
    "mobile network, network infrastructure & digital internet": "Communication Services",
    "telecommunications service & internet services": "Communication Services", "telecommunications": "Communication Services",
    "healthcare": "Health Care", "plantations": "Consumer Staples", "plantation": "Consumer Staples", "plantation & properties": "Consumer Staples",
    "agriculture": "Consumer Staples", "energy shipping, maritime services": "Energy", "food & beverages": "Consumer Staples",
    "chemicals": "Materials", "oil and gas": "Energy", "metals": "Materials", "conglomerate": "Industrials", "industrial": "Industrials",
    "electric power industry": "Utilities",
}
KLCI_OVERRIDES = {"6033": "Utilities"}  # Petronas Gas: gas transport and distribution
SET50_SECTOR = {
    "information & communication technology": "Communication Services", "transportation & logistics": "Industrials",
    "property development": "Real Estate", "energy & utilities": "Energy", "banking": "Financials", "health care services": "Health Care",
    "commerce": "Consumer Discretionary", "food & beverages": "Consumer Staples", "food & beverage": "Consumer Staples",
    "electronic components": "Information Technology", "petrochemicals & chemicals": "Materials", "finance & securities": "Financials",
    "construction materials": "Materials", "packaging": "Materials", "insurance": "Financials", "media & publishing": "Communication Services",
}
SET50_OVERRIDES = {"EGCO": "Utilities", "GPSC": "Utilities", "GULF": "Utilities", "RATCH": "Utilities", "CPALL": "Consumer Staples",
                   "BJC": "Consumer Staples", "MINT": "Consumer Discretionary"}
IBOV_SECTOR = {  # first level of B3's own sector classification
    "consumo não cíclico": "Consumer Staples", "consumo cíclico": "Consumer Discretionary", "bens industriais": "Industrials",
    "financeiro e outros": "Financials", "financeiro": "Financials", "materiais básicos": "Materials", "utilidade pública": "Utilities",
    "petróleo. gás e biocombustíveis": "Energy", "petróleo, gás e biocombustíveis": "Energy", "saúde": "Health Care",
    "tecnologia da informação": "Information Technology", "telecomunicações": "Communication Services",
}
LQ45_SECTOR = {
    "AADI": "Energy", "ADMR": "Materials", "ADRO": "Energy", "AKRA": "Energy", "AMMN": "Materials", "AMRT": "Consumer Staples",
    "ANTM": "Materials", "ASII": "Industrials", "BBCA": "Financials", "BBNI": "Financials", "BBRI": "Financials", "BBTN": "Financials",
    "BMRI": "Financials", "BRPT": "Materials", "BUMI": "Energy", "CPIN": "Consumer Staples", "CUAN": "Energy", "DEWA": "Energy",
    "EMTK": "Communication Services", "ESSA": "Materials", "EXCL": "Communication Services", "GOTO": "Consumer Discretionary",
    "HRTA": "Consumer Discretionary", "ICBP": "Consumer Staples", "INCO": "Materials", "INDF": "Consumer Staples", "INDY": "Energy",
    "INKP": "Materials", "ISAT": "Communication Services", "ITMG": "Energy", "JPFA": "Consumer Staples", "KLBF": "Health Care",
    "MAPI": "Consumer Discretionary", "MBMA": "Materials", "MDKA": "Materials", "MEDC": "Energy", "PGAS": "Utilities",
    "PGEO": "Utilities", "PTBA": "Energy", "SCMA": "Communication Services", "TLKM": "Communication Services", "UNTR": "Industrials",
    "UNVR": "Consumer Staples", "WIFI": "Communication Services",
}
IPC_OVERRIDES = {  # rows without a sector in the source
    "AMXB": "Communication Services", "BBAJIOO": "Financials", "CUERVO": "Consumer Staples", "GFINBURO": "Financials",
    "GFNORTEO": "Financials", "RA": "Financials",
}
NZX_SECTOR = {
    "airport services": "Industrials", "airlines": "Industrials", "diversified banks": "Financials", "diversified reits": "Real Estate",
    "retail reits": "Real Estate", "office reits": "Real Estate", "industrial reits": "Real Estate", "health care reits": "Real Estate",
    "packaged foods & meats": "Consumer Staples", "specialty stores": "Consumer Discretionary", "electric utilities": "Utilities",
    "renewable electricity": "Utilities", "multi-utilities": "Utilities", "oil and gas refining": "Energy",
    "alternative carriers": "Communication Services", "integrated telecommunication services": "Communication Services",
    "cable & satellite": "Communication Services", "health care distributors": "Health Care",
    "health care equipment & services": "Health Care", "health care facilities": "Health Care", "construction materials": "Materials",
    "air freight & logistics": "Industrials", "application software": "Information Technology", "consumer cyclical": "Consumer Discretionary",
    "marine ports & services": "Industrials", "financial exchanges & data": "Financials", "casinos & gaming": "Consumer Discretionary",
    "industrial machinery": "Industrials", "tourism": "Consumer Discretionary", "automotive": "Consumer Discretionary",
    "property & casualty insurance": "Financials", "metals & mining": "Materials",
}


def gics(label):
    """A GICS sector name as given, or its alias; "Unclassified" when neither."""
    return label if label in GICS_SECTORS else GICS_ALIASES.get(label.lower(), "Unclassified")


# Listings whose trading currency differs from the venue's (checked against the price provider 2026-10-24): the Jardine
# group companies trade in US dollars on the Singapore Exchange. Prices are only accepted in the instrument's currency.
# Keep in sync with Markets.ForeignCurrencyListings (the simulated provider quotes them in the same currency).
CURRENCY_OVERRIDES = {"D01.SI": "USD", "H78.SI": "USD", "J36.SI": "USD", "GSPARK.BD": "EUR", "HMSO.IR": "GBP"}

# Members renamed or re-listed since the pinned revision (checked against the price provider 2026-10-25): prices exist only
# under the current symbol. Source symbol -> (current ticker on the same venue, name shown).
TICKER_CHANGES = {
    "RY4C.IR": ("RYA", "Ryanair Holdings"),
    "IIA.VI": ("CPI", "CPI Europe (formerly Immofinanz)"),
    "SPL.WA": ("EBP", "Erste Bank Polska (formerly Santander Bank Polska)"),
    "CCC.WA": ("MDV", "Modivo (formerly CCC)"),
    "MYTIL.AT": ("MTLN", "Metlen Energy & Metals (formerly Mytilineos)"),
    "OPAP.AT": ("ALWN", "Allwyn (formerly OPAP)"),
    "KOJAMO.HE": ("LUMO", "Lumo Kodit (formerly Kojamo)"),
    "ALFAA.MX": ("SIGMAFA", "Sigma Foods (formerly Alfa)"),
    "CCRO3.SA": ("MOTV3", "Motiva (formerly CCR)"),
    "ELET3.SA": ("AXIA3", "Axia Energia (formerly Eletrobras)"),
    "EMBR3.SA": ("EMBJ3", "Embraer"),
    "NTCO3.SA": ("NATU3", "Natura"),
    "MRFG3.SA": ("MBRF3", "MBRF Global Foods (Marfrig and BRF combined)"),
}

# Rows of a source table that are not index constituents (removed and reported).
NOT_CONSTITUENTS = {
    "ASX50": {"VAS": "Vanguard Australian Shares is an index fund, not an ASX 50 company"},
    # No longer listed (no prices from the provider, 2026-10-25); the pinned revision predates the change.
    "OBX": {"GOGL": "Golden Ocean Group merged into CMB.TECH (2025)"},
    "PSI": {"SONC": "Sonae Capital was delisted after its takeover (2022)"},
    "ATHEX25": {"TENERGY": "Terna Energy was delisted after its takeover by Masdar (2025)"},
    "IPC": {"ELEKTRA": "Grupo Elektra is no longer listed"},
    "JSE40": {"MCG": "MultiChoice was delisted after its takeover by Canal+ (2025)"},
    "IBOV": {
        "BRFS3": "BRF was combined with Marfrig into MBRF Global Foods (MBRF3, listed)",
        "JBSS3": "JBS moved its primary listing to New York (2025); B3 trades only depositary receipts",
        "ELET6": "no quote for this preferred class under the new name (Axia Energia) at the provider",
        "AZUL4": "no quote for these preferred shares at the provider since the restructuring",
        "GOLL4": "no longer quoted at the provider",
        "CIEL3": "Cielo was delisted (2024)",
        "CRFB3": "Carrefour Brasil was delisted (2025)",
    },
}

# ------------------------------------------------------------------------------------------------- fetching


def fetch_once(url):
    request = urllib.request.Request(url, headers={"User-Agent": UA})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return json.load(response)
    except urllib.error.URLError as error:
        if "CERTIFICATE_VERIFY_FAILED" not in str(error):
            raise
        out = subprocess.run(["curl", "-sS", "--max-time", "60", "-A", UA, url], check=True, capture_output=True)
        return json.loads(out.stdout)


def fetch_json(url, attempts=6):
    """Wikipedia throttles bursts of requests (an empty or HTML answer): wait and retry with a growing pause."""
    for attempt in range(attempts):
        try:
            return fetch_once(url)
        except (ValueError, urllib.error.HTTPError, subprocess.CalledProcessError):
            if attempt == attempts - 1:
                raise
            time.sleep(5 * (attempt + 1))


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
    # The class may follow other attributes (<table style="…" class="wikitable sortable">).
    return re.findall(r'<table[^>]*\sclass="[^"]*wikitable[^"]*"[^>]*>(.*?)</table>', text, re.S)


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
        # A one-cell title row above the real header ("TA-35 Components") is skipped.
        if len(parsed) > 1 and len(parsed[0][0]) == 1 and all(k == "h" for k in parsed[1][2]):
            parsed = parsed[1:]
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


def after_colon(cell):
    """'OSE: AKRBP' / 'Euronext Brussels: ABI' -> 'AKRBP' / 'ABI'."""
    return cell.split(":")[-1].strip().upper()


def obx(text):
    header, data = find_table(text, "company", "ticker symbol", "icb subsector")
    c, t, sec = col(header, "company"), col(header, "ticker symbol"), col(header, "icb subsector")
    return [market_row("NO", after_colon(cells[t]), name_of(cells[c]), OBX_SECTOR.get(cells[sec].lower(), "Unclassified"),
                       cells[sec][:1].upper() + cells[sec][1:], wiki_title(raw[c])) for cells, raw, _ in data]


def omxh25(text):
    header, data = find_table(text, "ticker", "company", "gics")
    t, c, sec = col(header, "ticker"), col(header, "company"), col(header, "gics")
    return [market_row("FI", cells[t].upper().removesuffix(".HE"), name_of(cells[c]), gics(cells[sec]), cells[sec], wiki_title(raw[c]))
            for cells, raw, _ in data]


def bel20(text):
    header, data = find_table(text, "company", "ticker symbol", "weighting")
    c, t, sec = col(header, "company"), col(header, "ticker symbol"), col(header, "icb")
    out = []
    for cells, raw, _ in data:
        # Members listed elsewhere keep their own exchange (Aperam: Euronext Amsterdam, APAM.AS).
        country = "NL" if "amsterdam" in cells[t].lower() else "BE"
        out.append(market_row(country, after_colon(cells[t]), name_of(cells[c]), gics(cells[sec]), cells[sec], wiki_title(raw[c])))
    return out


def psi(text):
    header, data = find_table(text, "company", "ticker", "weight")
    c, t, sec = col(header, "company"), col(header, "ticker"), col(header, "industry")
    return [market_row("PT", cells[t].upper(), name_of(cells[c]), PSI_SECTOR.get(cells[sec].lower(), "Unclassified"), cells[sec],
                       wiki_title(raw[c])) for cells, raw, _ in data]


def iseq20(text):
    header, data = find_table(text, "mnem", "company")
    t, c = col(header, "mnem"), col(header, "company")
    out = []
    for cells, raw, _ in data:
        ticker = after_colon(cells[t])
        out.append(market_row("IE", ticker, name_of(cells[c]), ISEQ_SECTOR.get(ticker, "Unclassified"), "", wiki_title(raw[c])))
    return out


def by_name(country, members, code):
    """Rows of a table without tickers: company name -> (ticker, sector) from the curated mapping; an unknown name stops the build."""
    def parse(text, name_col, industry_col):
        header, data = find_table(text, name_col, industry_col)
        c, ind = col(header, name_col), col(header, industry_col)
        out = []
        for cells, raw, _ in data:
            name = name_of(cells[c])
            if name not in members:
                raise SystemExit(f"{code}: no ticker for '{name}' – add it to the mapping after checking it with the price provider")
            ticker, sector = members[name]
            out.append(market_row(country, ticker, name, sector, cells[ind], wiki_title(raw[c])))
        return out
    return parse


def atx(text):
    return by_name("AT", ATX_MEMBERS, "ATX")(text, "company", "industry")


def wig20(text):
    return by_name("PL", WIG20_MEMBERS, "WIG20")(text, "unternehmen", "branche")


def jse40(text):
    return by_name("ZA", JSE40_MEMBERS, "JSE40")(text, "name", "sector")


def bux(text):
    header, data = find_table(text, "company", "ticker", "icb")
    c, t, sec = col(header, "company"), col(header, "ticker"), col(header, "icb")
    return [market_row("HU", cells[t].upper(), name_of(cells[c]), BUX_SECTOR.get(cells[sec].lower(), "Unclassified"), cells[sec],
                       wiki_title(raw[c])) for cells, raw, _ in data]


def athex25(text):
    header, data = find_table(text, "company", "traded as", "sector")
    c, t, sec = col(header, "company"), col(header, "traded as"), col(header, "sector")
    return [market_row("GR", after_colon(cells[t]), name_of(cells[c]), ATHEX_SECTOR.get(cells[sec].lower(), "Unclassified"), cells[sec],
                       wiki_title(raw[c])) for cells, raw, _ in data]


def omxi15(text):
    header, data = find_table(text, "company", "symbol", "gics")
    c, t, sec = col(header, "company"), col(header, "symbol"), col(header, "gics")
    return [market_row("IS", cells[t].upper(), name_of(cells[c]), gics(cells[sec]), cells[sec], wiki_title(raw[c])) for cells, raw, _ in data]


def ta35(text):
    header, data = find_table(text, "corporation", "ticker", "industry")
    c, t, sec = col(header, "corporation"), col(header, "ticker"), col(header, "industry")
    return [market_row("IL", cells[t].upper(), name_of(cells[c]), TA35_SECTOR.get(cells[sec].lower(), "Unclassified"), cells[sec],
                       wiki_title(raw[c])) for cells, raw, _ in data]


def kospi200(text):
    header, data = find_table(text, "company", "symbol", "gics")
    c, t, sec = col(header, "company"), col(header, "symbol"), col(header, "gics")
    return [market_row("KR", re.sub(r"\D", "", cells[t]).zfill(6), name_of(cells[c]), gics(cells[sec]), cells[sec], wiki_title(raw[c]))
            for cells, raw, _ in data]


def sse50(text):
    header, data = find_table(text, "name", "ticker")
    c, t = col(header, "name"), col(header, "ticker")
    out = []
    for cells, raw, _ in data:
        code = int(re.sub(r"\D", "", cells[t]))
        out.append(market_row("CN", str(code), name_of(cells[c]), SSE50_SECTOR.get(code, "Unclassified"), "", wiki_title(raw[c])))
    return out


def klci(text):
    header, data = find_table(text, "constituent", "stock code", "sector")
    c, t, sec = col(header, "constituent"), col(header, "stock code"), col(header, "sector")
    out = []
    for cells, raw, _ in data:
        code = re.sub(r"\D", "", cells[t]).zfill(4)
        sector = KLCI_OVERRIDES.get(code) or KLCI_SECTOR.get(cells[sec].lower(), "Unclassified")
        # The source writes most names in capitals; they are kept as published.
        out.append(market_row("MY", code, name_of(cells[c]), sector, cells[sec], wiki_title(raw[c])))
    return out


def set50(text):
    header, data = find_table(text, "symbol", "securities name", "sector")
    t, c, sec = col(header, "symbol"), col(header, "securities name"), col(header, "sector")
    out = []
    for cells, raw, _ in data:
        ticker = cells[t].upper()
        sector = SET50_OVERRIDES.get(ticker) or SET50_SECTOR.get(cells[sec].lower(), "Unclassified")
        out.append(market_row("TH", ticker, name_of(cells[c]), sector, cells[sec], wiki_title(raw[c])))
    return out


def ibov(text):
    header, data = find_table(text, "código", "ação", "setor")
    t, c, sec = col(header, "código"), col(header, "ação"), col(header, "setor")
    out, previous = [], None
    for cells, raw, _ in data:
        # A second share class of the same company is a short row (code and class only): it takes name and sector from above.
        if len(cells) <= 2 and previous:
            name, label, wiki = previous
        else:
            name, label, wiki = name_of(cells[c]), cells[sec], wiki_title(raw[c])
            previous = (name, label, wiki)
        top = label.split("/")[0].strip().lower()
        sector = "Real Estate" if "imóveis" in label.lower() else IBOV_SECTOR.get(top, "Unclassified")
        out.append(market_row("BR", cells[t].upper(), name, sector, label, wiki))
    return out


def lq45(text):
    header, data = find_table(text, "ticker", "company")
    t, c = col(header, "ticker"), col(header, "company")
    out = []
    for cells, raw, _ in data:
        ticker = after_colon(cells[t])
        out.append(market_row("ID", ticker, name_of(cells[c]), LQ45_SECTOR.get(ticker, "Unclassified"), "", wiki_title(raw[c])))
    return out


def ipc(text):
    header, data = find_table(text, "name", "symbol", "sector", "sub-industry")
    c, t, sec, sub = col(header, "name"), col(header, "symbol"), col(header, "sector"), col(header, "sub-industry")
    out = []
    for cells, raw, _ in data:
        # Series letters are part of the Yahoo symbol without a space ("ALFA A" -> ALFAA.MX).
        ticker = cells[t].upper().replace(" ", "")
        sector = IPC_OVERRIDES.get(ticker) or gics(cells[sec])
        out.append(market_row("MX", ticker, name_of(cells[c]), sector, cells[sub] or cells[sec], wiki_title(raw[c])))
    return out


def nzx50(text):
    header, data = find_table(text, "ticker symbol", "company", "sector")
    t, c, sec = col(header, "ticker symbol"), col(header, "company"), col(header, "sector")
    return [market_row("NZ", cells[t].upper(), name_of(cells[c]), NZX_SECTOR.get(cells[sec].lower(), "Unclassified"), cells[sec],
                       wiki_title(raw[c])) for cells, raw, _ in data]


def market_row(country, ticker, name, sector, industry, wiki="", isin=""):
    m = MARKET[country]
    symbol = ticker + m["suffix"]
    return dict(symbol=symbol, name=name, country=country, exchange=m["exchange"], mic=m["mic"],
                currency=CURRENCY_OVERRIDES.get(symbol, m["currency"]), sector=sector, industry=industry, wiki=wiki, isin=isin)


PARSERS = {"SP500": sp500, "DAX40": dax, "DAX30": dax, "CAC40": cac40, "FTSE100": ftse100, "NIKKEI225": nikkei225,
           "SMI": smi, "IBEX35": ibex35, "FTSEMIB": ftsemib, "OMXS30": omxs30, "OMXC25": omxc25, "TSX60": tsx60, "ASX50": asx50,
           "HSI": hsi, "NIFTY50": nifty50, "STI": sti,
           "OBX": obx, "OMXH25": omxh25, "BEL20": bel20, "PSI": psi, "ISEQ20": iseq20, "ATX": atx, "WIG20": wig20, "BUX": bux,
           "ATHEX25": athex25, "OMXI15": omxi15, "TA35": ta35, "KOSPI200": kospi200, "SSE50": sse50, "KLCI": klci, "SET50": set50,
           "IBOV": ibov, "LQ45": lq45, "IPC": ipc, "JSE40": jse40, "NZX50": nzx50}

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
            if m["symbol"] in TICKER_CHANGES:
                ticker, name = TICKER_CHANGES[m["symbol"]]
                m = market_row(m["country"], ticker, name, m["sector"], m["industry"], m["wiki"], m.get("isin", ""))
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
                "verified_on": VERIFIED_ETFS_BY_COUNTRY.get(country, VERIFIED_ETFS),
                "description": "Exchange-traded funds that track a local index – a whole market in one instrument. Not index constituents.",
            }
            print(f"{code}: {len(funds)} funds (curated, checked {index_rows[code]['verified_on']})")

    with open(indices_path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=["code", "name", "country", "currency", "benchmark", "is_historical", "members", "source",
                                               "source_url", "revision", "licence", "verified_on", "description"], lineterminator="\n")
        writer.writeheader()
        for code in list(INDICES) + [f"ETF{c}" for c in ETFS]:
            if code in index_rows:
                writer.writerow(index_rows[code])


if __name__ == "__main__":
    main()
