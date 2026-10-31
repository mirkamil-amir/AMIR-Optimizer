/**
 * Trading venues of the catalogue (mirror of `Markets.Venues` on the server) with the city of each exchange, used to
 * place markets on the background world map. Canonical symbols carry the venue suffix (SAP.DE, 0005.HK); US listings
 * have none. The map shows where a security is *listed* (its exchange) – not the company's headquarters or where it
 * earns its revenue, which the catalogue does not record.
 */

/** Geographic box in degrees: west, south, east, north longitude/latitude. */
export type GeoBox = readonly [west: number, south: number, east: number, north: number]

export interface Venue {
  country: string
  countryName: string
  exchange: string
  suffix: string
  city: string
  lat: number
  lon: number
  /** ISO 3166-1 numeric code of the country outline on the map (Natural Earth 1:50m). */
  iso: string
  /** Region the map frames when this market is selected on its own (a European listing brings Europe into view). */
  frame: GeoBox
  /** Side of the marker the city label is drawn on (keeps the dense European labels apart). */
  label: 'left' | 'right' | 'above' | 'below'
}

const EUROPE: GeoBox = [-11, 35, 31, 66]

export const venues: Venue[] = [
  { country: 'US', countryName: 'United States', exchange: 'NYSE / NASDAQ', suffix: '', city: 'New York', lat: 40.71, lon: -74.01, iso: '840', frame: [-125, 24, -66, 50], label: 'right' },
  { country: 'DE', countryName: 'Germany', exchange: 'XETRA', suffix: '.DE', city: 'Frankfurt', lat: 50.11, lon: 8.68, iso: '276', frame: EUROPE, label: 'right' },
  { country: 'FR', countryName: 'France', exchange: 'Euronext Paris', suffix: '.PA', city: 'Paris', lat: 48.86, lon: 2.35, iso: '250', frame: EUROPE, label: 'left' },
  { country: 'NL', countryName: 'Netherlands', exchange: 'Euronext Amsterdam', suffix: '.AS', city: 'Amsterdam', lat: 52.37, lon: 4.9, iso: '528', frame: EUROPE, label: 'above' },
  { country: 'GB', countryName: 'United Kingdom', exchange: 'London Stock Exchange', suffix: '.L', city: 'London', lat: 51.51, lon: -0.13, iso: '826', frame: EUROPE, label: 'left' },
  { country: 'JP', countryName: 'Japan', exchange: 'Tokyo Stock Exchange', suffix: '.T', city: 'Tokyo', lat: 35.68, lon: 139.69, iso: '392', frame: [127, 30, 147, 46], label: 'right' },
  { country: 'CH', countryName: 'Switzerland', exchange: 'SIX Swiss Exchange', suffix: '.SW', city: 'Zurich', lat: 47.37, lon: 8.54, iso: '756', frame: EUROPE, label: 'below' },
  { country: 'ES', countryName: 'Spain', exchange: 'Bolsa de Madrid', suffix: '.MC', city: 'Madrid', lat: 40.42, lon: -3.7, iso: '724', frame: EUROPE, label: 'left' },
  { country: 'IT', countryName: 'Italy', exchange: 'Borsa Italiana', suffix: '.MI', city: 'Milan', lat: 45.46, lon: 9.19, iso: '380', frame: EUROPE, label: 'right' },
  { country: 'SE', countryName: 'Sweden', exchange: 'Nasdaq Stockholm', suffix: '.ST', city: 'Stockholm', lat: 59.33, lon: 18.07, iso: '752', frame: EUROPE, label: 'right' },
  { country: 'DK', countryName: 'Denmark', exchange: 'Nasdaq Copenhagen', suffix: '.CO', city: 'Copenhagen', lat: 55.68, lon: 12.57, iso: '208', frame: EUROPE, label: 'right' },
  { country: 'CA', countryName: 'Canada', exchange: 'Toronto Stock Exchange', suffix: '.TO', city: 'Toronto', lat: 43.65, lon: -79.38, iso: '124', frame: [-128, 41, -55, 60], label: 'above' },
  { country: 'AU', countryName: 'Australia', exchange: 'Australian Securities Exchange', suffix: '.AX', city: 'Sydney', lat: -33.87, lon: 151.21, iso: '036', frame: [112, -44, 155, -10], label: 'right' },
  { country: 'HK', countryName: 'Hong Kong', exchange: 'Hong Kong Stock Exchange', suffix: '.HK', city: 'Hong Kong', lat: 22.32, lon: 114.17, iso: '344', frame: [104, 14, 126, 32], label: 'right' },
  { country: 'IN', countryName: 'India', exchange: 'National Stock Exchange of India', suffix: '.NS', city: 'Mumbai', lat: 19.08, lon: 72.88, iso: '356', frame: [67, 6, 92, 32], label: 'left' },
  { country: 'SG', countryName: 'Singapore', exchange: 'Singapore Exchange', suffix: '.SI', city: 'Singapore', lat: 1.35, lon: 103.82, iso: '702', frame: [96, -7, 112, 9], label: 'below' },
  // Twenty markets added 2026-10-25.
  { country: 'NO', countryName: 'Norway', exchange: 'Oslo Børs', suffix: '.OL', city: 'Oslo', lat: 59.91, lon: 10.75, iso: '578', frame: EUROPE, label: 'left' },
  { country: 'FI', countryName: 'Finland', exchange: 'Nasdaq Helsinki', suffix: '.HE', city: 'Helsinki', lat: 60.17, lon: 24.94, iso: '246', frame: EUROPE, label: 'right' },
  { country: 'BE', countryName: 'Belgium', exchange: 'Euronext Brussels', suffix: '.BR', city: 'Brussels', lat: 50.85, lon: 4.35, iso: '056', frame: EUROPE, label: 'below' },
  { country: 'PT', countryName: 'Portugal', exchange: 'Euronext Lisbon', suffix: '.LS', city: 'Lisbon', lat: 38.72, lon: -9.14, iso: '620', frame: EUROPE, label: 'left' },
  { country: 'IE', countryName: 'Ireland', exchange: 'Euronext Dublin', suffix: '.IR', city: 'Dublin', lat: 53.35, lon: -6.26, iso: '372', frame: EUROPE, label: 'left' },
  { country: 'AT', countryName: 'Austria', exchange: 'Wiener Börse', suffix: '.VI', city: 'Vienna', lat: 48.21, lon: 16.37, iso: '040', frame: EUROPE, label: 'right' },
  { country: 'PL', countryName: 'Poland', exchange: 'Warsaw Stock Exchange', suffix: '.WA', city: 'Warsaw', lat: 52.23, lon: 21.01, iso: '616', frame: EUROPE, label: 'right' },
  { country: 'HU', countryName: 'Hungary', exchange: 'Budapest Stock Exchange', suffix: '.BD', city: 'Budapest', lat: 47.5, lon: 19.04, iso: '348', frame: EUROPE, label: 'below' },
  { country: 'GR', countryName: 'Greece', exchange: 'Athens Stock Exchange', suffix: '.AT', city: 'Athens', lat: 37.98, lon: 23.73, iso: '300', frame: EUROPE, label: 'right' },
  { country: 'IS', countryName: 'Iceland', exchange: 'Nasdaq Iceland', suffix: '.IC', city: 'Reykjavik', lat: 64.15, lon: -21.94, iso: '352', frame: [-28, 50, 12, 68], label: 'right' },
  { country: 'IL', countryName: 'Israel', exchange: 'Tel Aviv Stock Exchange', suffix: '.TA', city: 'Tel Aviv', lat: 32.09, lon: 34.78, iso: '376', frame: [26, 26, 44, 38], label: 'right' },
  { country: 'KR', countryName: 'South Korea', exchange: 'Korea Exchange', suffix: '.KS', city: 'Seoul', lat: 37.57, lon: 126.98, iso: '410', frame: [119, 32, 133, 41], label: 'left' },
  { country: 'CN', countryName: 'China', exchange: 'Shanghai Stock Exchange', suffix: '.SS', city: 'Shanghai', lat: 31.23, lon: 121.47, iso: '156', frame: [100, 18, 128, 42], label: 'right' },
  { country: 'MY', countryName: 'Malaysia', exchange: 'Bursa Malaysia', suffix: '.KL', city: 'Kuala Lumpur', lat: 3.14, lon: 101.69, iso: '458', frame: [96, -3, 120, 9], label: 'left' },
  { country: 'TH', countryName: 'Thailand', exchange: 'Stock Exchange of Thailand', suffix: '.BK', city: 'Bangkok', lat: 13.76, lon: 100.5, iso: '764', frame: [95, 4, 108, 21], label: 'left' },
  { country: 'BR', countryName: 'Brazil', exchange: 'B3', suffix: '.SA', city: 'São Paulo', lat: -23.55, lon: -46.63, iso: '076', frame: [-75, -35, -33, 6], label: 'right' },
  { country: 'ID', countryName: 'Indonesia', exchange: 'Indonesia Stock Exchange', suffix: '.JK', city: 'Jakarta', lat: -6.21, lon: 106.85, iso: '360', frame: [94, -12, 142, 7], label: 'below' },
  { country: 'MX', countryName: 'Mexico', exchange: 'Bolsa Mexicana de Valores', suffix: '.MX', city: 'Mexico City', lat: 19.43, lon: -99.13, iso: '484', frame: [-118, 14, -86, 33], label: 'left' },
  { country: 'ZA', countryName: 'South Africa', exchange: 'Johannesburg Stock Exchange', suffix: '.JO', city: 'Johannesburg', lat: -26.2, lon: 28.05, iso: '710', frame: [15, -35, 34, -21], label: 'right' },
  { country: 'NZ', countryName: 'New Zealand', exchange: 'New Zealand Exchange', suffix: '.NZ', city: 'Wellington', lat: -41.29, lon: 174.78, iso: '554', frame: [165, -48, 180, -33], label: 'left' },
]

export const venueByCountry: ReadonlyMap<string, Venue> = new Map(venues.map((v) => [v.country, v]))

/** Regions that group the markets in lists (the stock chooser), in display order. */
export const regions = ['Americas', 'Europe', 'Asia-Pacific', 'Middle East & Africa'] as const
export type Region = (typeof regions)[number]

const regionOfCountry: Record<string, Region> = {
  US: 'Americas', CA: 'Americas', BR: 'Americas', MX: 'Americas',
  IL: 'Middle East & Africa', ZA: 'Middle East & Africa',
  JP: 'Asia-Pacific', AU: 'Asia-Pacific', HK: 'Asia-Pacific', IN: 'Asia-Pacific', SG: 'Asia-Pacific', KR: 'Asia-Pacific',
  CN: 'Asia-Pacific', MY: 'Asia-Pacific', TH: 'Asia-Pacific', ID: 'Asia-Pacific', NZ: 'Asia-Pacific',
}

/** Region of a market (ISO country of its exchange); every European venue not listed above is in Europe. */
export function regionOf(country: string): Region {
  return regionOfCountry[country] ?? 'Europe'
}

/** Benchmark index symbols (no venue suffix) and their market – mirror of `Markets.IndexVenues`. */
const indexCountries: Record<string, string> = {
  '^GSPC': 'US', '^GDAXI': 'DE', '^FCHI': 'FR', '^FTSE': 'GB', '^N225': 'JP', '^SSMI': 'CH', '^IBEX': 'ES', '^OMX': 'SE',
  '^OMXC25': 'DK', '^GSPTSE': 'CA', '^AXJO': 'AU', '^HSI': 'HK', '^NSEI': 'IN', '^STI': 'SG',
  '^OMXH25': 'FI', '^BFX': 'BE', '^ISEQ': 'IE', '^ATX': 'AT', '^KS11': 'KR', '^KLSE': 'MY', '^BVSP': 'BR', '^JKLQ45': 'ID',
  '^MXX': 'MX', '^J200.JO': 'ZA', '^NZ50': 'NZ',
}

const resolved = new Map<string, string | null>()
/** Symbols whose market could not be resolved (for diagnosis; never placed on the map). */
export const unresolvedSymbols = new Set<string>()

function resolve(s: string): string | null {
  if (indexCountries[s]) return indexCountries[s]
  if (s.startsWith('^')) return null // an index the catalogue does not know
  const dot = s.lastIndexOf('.')
  if (dot > 0) {
    const suffix = s.slice(dot)
    const venue = venues.find((v) => v.suffix === suffix)
    if (venue) return venue.country
    // US share classes use '-' (BRK-B); a dot with an unknown suffix is a venue we do not support.
    return null
  }
  return 'US' // the server's convention: no suffix = US listing
}

/**
 * Market (ISO country of the listing exchange) of a canonical symbol, following the server's symbol convention
 * (`Markets.ForSymbol`): venue suffix, known index, no suffix = United States. Unknown suffixes give null – they are
 * recorded in `unresolvedSymbols`, never guessed. Results are cached.
 */
export function countryOfSymbol(symbol: string): string | null {
  const s = symbol.trim().toUpperCase()
  let country = resolved.get(s)
  if (country === undefined) {
    country = resolve(s)
    resolved.set(s, country)
    if (country === null) unresolvedSymbols.add(s)
  }
  return country
}

/** Distinct markets of a list of symbols, in venue order; unresolved symbols are left out. */
export function countriesOf(symbols: Iterable<string>): string[] {
  const set = new Set<string>()
  for (const s of symbols) {
    const country = countryOfSymbol(s)
    if (country) set.add(country)
  }
  return venues.filter((v) => set.has(v.country)).map((v) => v.country)
}

export const countryNames: Record<string, string> = Object.fromEntries(venues.map((v) => [v.country, v.countryName]))

/** Display name of an index code (SP500 → "S&P 500") from the catalogue's markets; the code itself when unknown. */
export function indexName(markets: { indices: { code: string; name: string }[] }[] | undefined, code: string): string {
  return markets?.flatMap((m) => m.indices).find((i) => i.code === code)?.name ?? code
}

/** "Industrials · DAX 40, Euro Stoxx 50": a stock's sector and the indices it belongs to (as listed in the catalogue). */
export function sectorAndIndices(sector: string | null, indices: string[], markets: Parameters<typeof indexName>[0]): string {
  return [sector ?? 'Unclassified', indices.map((i) => indexName(markets, i)).join(', ')].filter(Boolean).join(' · ')
}
