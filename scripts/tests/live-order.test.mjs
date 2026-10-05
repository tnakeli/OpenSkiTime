import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const context = {};
vm.runInNewContext(readFileSync(new URL('../../src/OpenSkiTime.LiveTiming.Server/wwwroot/live-order.js', import.meta.url), 'utf8'), context);
const order = (rows, startOrder) => context.liveResultOrder(rows, startOrder).map(row => row.bib);

test('equal ranks are listed with the higher bib first, independent of start order', () => {
  const startOrder = [1, 2, 3, 4, 5, 6, 7];
  const rows = [
    { bib: 1, rank: 1 }, { bib: 2, rank: 4 }, { bib: 3, rank: 1 }, { bib: 4, rank: 4 },
    { bib: 5, rank: 4 }, { bib: 6, rank: 7 }, { bib: 7, rank: 1 }
  ];
  assert.deepEqual(order(rows, startOrder), [7, 3, 1, 5, 4, 2, 6]);
  assert.deepEqual(order(rows.slice().reverse(), startOrder.slice().reverse()), [7, 3, 1, 5, 4, 2, 6]);
});

test('rows without a rank follow the ranked rows in start order and nothing is dropped', () => {
  const startOrder = [30, 10, 20, 40, 50];
  const rows = [{ bib: 10 }, { bib: 50, rank: 2 }, { bib: 20 }, { bib: 40, rank: 1 }, { bib: 30 }];
  assert.deepEqual(order(rows, startOrder), [40, 50, 30, 10, 20]);
  assert.equal(context.liveResultOrder(rows, startOrder).length, rows.length);
});
