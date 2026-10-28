import assert from 'node:assert/strict';
import { test } from 'node:test';
import { compareAmounts, formatAmount } from '../src/app/amount.ts';

for (const [amount, expected] of [
  ['29.99', '29.99'],
  ['0.00', '0.00'],
  ['0.01', '0.01'],
  ['-0.01', '-0.01'],
  ['-12.34', '-12.34'],
  ['1234.50', '1,234.50'],
  ['-1234.50', '-1,234.50'],
  ['9999999999999999.99', '9,999,999,999,999,999.99'],
  ['-9999999999999999.99', '-9,999,999,999,999,999.99'],
  ['9999999999999999.98', '9,999,999,999,999,999.98'],
]) {
  test(`formats ${amount} without losing cents`, () => {
    assert.equal(formatAmount(amount), expected);
  });
}

const ascending = [
  '-9999999999999999.99', '-9999999999999999.98', '-12.34', '-0.01',
  '0.00', '0.01', '29.99', '90071992547409.91', '90071992547409.92',
  '9999999999999999.98', '9999999999999999.99',
];

test('sorts normal, signed, maximum, and adjacent large amounts ascending', () => {
  assert.deepEqual([...ascending].reverse().sort(compareAmounts), ascending);
});

test('sorts amounts descending while preserving one-cent differences', () => {
  assert.deepEqual([...ascending].sort((left, right) => -compareAmounts(left, right)),
    [...ascending].reverse());
});

test('equal amounts preserve the existing secondary sort order', () => {
  const rows = [
    { amount: '29.99', sourceSystem: 'Store Uptown', transactionId: 102 },
    { amount: '29.99', sourceSystem: 'Online', transactionId: 102 },
    { amount: '29.99', sourceSystem: 'Online', transactionId: 101 },
  ];
  rows.sort((left, right) => compareAmounts(left.amount, right.amount)
    || left.sourceSystem.localeCompare(right.sourceSystem, 'en')
    || left.transactionId - right.transactionId);
  assert.deepEqual(rows.map(row => [row.sourceSystem, row.transactionId]),
    [['Online', 101], ['Online', 102], ['Store Uptown', 102]]);
});
