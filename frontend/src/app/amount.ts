// The API supplies invariant decimal strings with exactly two fractional digits.
export function formatAmount(amount: string): string {
  const [whole, fraction] = amount.split('.');
  return `${whole.replace(/\B(?=(\d{3})+(?!\d))/g, ',')}.${fraction}`;
}

export function compareAmounts(left: string, right: string): number {
  const leftCents = BigInt(left.replace('.', ''));
  const rightCents = BigInt(right.replace('.', ''));
  return leftCents < rightCents ? -1 : leftCents > rightCents ? 1 : 0;
}
