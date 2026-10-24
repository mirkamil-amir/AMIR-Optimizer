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
  /** ISO 3166-1 numeric code of the country outline on the map; null when the market is too small for the map's scale. */
  iso: string | null
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
  { country: 'HK', countryName: 'Hong Kong', exchange: 'Hong Kong Stock Exchange', suffix: '.HK', city: 'Hong Kong', lat: 22.32, lon: 114.17, iso: null, frame: [104, 14, 126, 32], label: 'right' },
  { country: 'IN', countryName: 'India', exchange: 'National Stock Exchange of India', suffix: '.NS', city: 'Mumbai', lat: 19.08, lon: 72.88, iso: '356', frame: [67, 6, 92, 32], label: 'left' },
  { country: 'SG', countryName: 'Singapore', exchange: 'Singapore Exchange', suffix: '.SI', city: 'Singapore', lat: 1.35, lon: 103.82, iso: null, frame: [96, -7, 112, 9], label: 'below' },
]

export const venueByCountry: ReadonlyMap<string, Venue> = new Map(venues.map((v) => [v.country, v]))

/** Benchmark index symbols (no venue suffix) and their market – mirror of `Markets.IndexVenues`. */
const indexCountries: Record<string, string> = {
  '^GSPC': 'US', '^GDAXI': 'DE', '^FCHI': 'FR', '^FTSE': 'GB', '^N225': 'JP', '^SSMI': 'CH', '^IBEX': 'ES', '^OMX': 'SE',
  '^OMXC25': 'DK', '^GSPTSE': 'CA', '^AXJO': 'AU', '^HSI': 'HK', '^NSEI': 'IN', '^STI': 'SG',
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
