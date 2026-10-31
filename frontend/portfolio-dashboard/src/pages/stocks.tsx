import { useState } from 'react'
import { PageHeader } from '@/components/common'
import { CreatePortfolioPanel } from '@/components/create-portfolio'
import { StockChooser } from '@/components/stock-chooser'
import { useSettings } from '@/lib/queries'

export function StocksPage() {
  const settings = useSettings()
  // One portfolio currency for the form and the stock list: changing it in either place changes both.
  const [chosen, setChosen] = useState('')
  const currency = chosen || settings.data?.defaultCurrency || 'USD'
  return (
    <>
      <PageHeader
        title="New portfolio"
        info="Create a new portfolio in three steps: describe it, choose the stocks it may invest in, and create it. Nothing is saved until you click Forge portfolio; it then holds only simulated cash – nothing is bought until you confirm a trade proposal or record a transaction. The stock selection is temporary: it stays in this browser tab only."
      />
      <div className="mb-6"><CreatePortfolioPanel currency={currency} onCurrencyChange={setChosen} /></div>
      <h2 className="heading-display mb-3 text-[1.3rem]">Choose stocks</h2>
      <StockChooser priceCurrency={currency} onPriceCurrencyChange={setChosen} />
    </>
  )
}
