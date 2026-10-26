import { PageHeader } from '@/components/common'
import { CreatePortfolioPanel } from '@/components/create-portfolio'
import { StockChooser } from '@/components/stock-chooser'

export function StocksPage() {
  return (
    <>
      <PageHeader
        title="New portfolio"
        info="Create a new portfolio in three steps: describe it, choose the stocks it may invest in, and create it. Nothing is saved until you click Create portfolio; it then holds only simulated cash – nothing is bought until you confirm a trade proposal or record a transaction. The stock selection is temporary: it stays in this browser tab only."
      />
      <div className="mb-6"><CreatePortfolioPanel /></div>
      <h2 className="heading-display mb-3 text-[1.3rem]">Choose stocks</h2>
      <StockChooser />
    </>
  )
}
