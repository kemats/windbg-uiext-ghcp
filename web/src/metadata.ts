export function number(value: number | null | undefined): string {
  return value == null || !Number.isFinite(value) ? 'Not reported' : value.toLocaleString(undefined, { maximumFractionDigits: 0 })
}

export function credits(nanoAiu: number | null | undefined, missing = 0): string {
  if (nanoAiu == null || !Number.isFinite(nanoAiu) || nanoAiu < 0) return 'Not reported'
  return `${(nanoAiu / 1_000_000_000).toLocaleString(undefined, { maximumFractionDigits: 4 })} credits${missing ? ' (partial)' : ''}`
}

export function time(value: string | null | undefined): string {
  if (!value || !Number.isFinite(Date.parse(value))) return 'Not reported'
  return new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'medium' })
}

export function percent(current: number | null | undefined, limit: number | null | undefined): number | null {
  return current != null && limit != null && Number.isFinite(current) && Number.isFinite(limit) && limit > 0
    ? Math.min(100, Math.max(0, current / limit * 100)) : null
}

export function pricePerMillion(price: number | null | undefined, batch: number | null | undefined): string {
  if (price == null || batch == null || !Number.isFinite(price) || !Number.isFinite(batch) || price < 0 || batch <= 0) return 'Not reported'
  return (price / batch * 1_000_000).toLocaleString(undefined, { maximumFractionDigits: 4 })
}