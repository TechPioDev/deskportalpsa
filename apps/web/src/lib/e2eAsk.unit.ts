import { test, expect, type APIResponse } from '@playwright/test';
import { ask, connectionWasReset } from '../../e2e/support/ask';

/**
 * The helper every browser spec sends its own requests through: what it sends again, how often,
 * and what it lets through at once.
 */

const answer = (status: number) => ({ status: () => status }) as unknown as APIResponse;
const noWait = () => Promise.resolve();
const reset = () => new Error('apiRequestContext.get: read ECONNRESET');

test('an answer that is not "too many requests" is returned as it is, asked once', async () => {
  let sent = 0;
  const res = await ask(async () => { sent++; return answer(403); }, noWait);
  expect(res.status()).toBe(403);
  expect(sent).toBe(1);
});

test('a reset connection is sent again and the answer returned', async () => {
  let sent = 0;
  const waits: number[] = [];
  const res = await ask(async () => { if (++sent <= 2) throw reset(); return answer(200); }, async (ms) => { waits.push(ms); });
  expect(res.status()).toBe(200);
  expect(sent).toBe(3);
  expect(waits).toEqual([250, 500]);
});

test('a connection that keeps being reset is given up on after three more tries, with the error itself', async () => {
  let sent = 0;
  await expect(ask(async () => { sent++; throw reset(); }, noWait)).rejects.toThrow('ECONNRESET');
  expect(sent).toBe(4);
});

test('any other failure is not sent again', async () => {
  let sent = 0;
  await expect(ask(async () => { sent++; throw new Error('apiRequestContext.get: Target page, context or browser has been closed'); }, noWait))
    .rejects.toThrow('has been closed');
  expect(sent).toBe(1);
});

test('"too many requests" is waited out eight times, then returned for the test to see', async () => {
  let sent = 0;
  const waits: number[] = [];
  const res = await ask(async () => { sent++; return answer(429); }, async (ms) => { waits.push(ms); });
  expect(res.status()).toBe(429);
  expect(sent).toBe(9);
  expect(waits).toEqual(Array(8).fill(10_000));
});

test('a limit and a reset in one exchange are each counted on their own', async () => {
  const script = [() => answer(429), () => { throw reset(); }, () => answer(429), () => answer(201)];
  let turn = 0;
  const res = await ask(async () => script[turn++](), noWait);
  expect(res.status()).toBe(201);
  expect(turn).toBe(4);
});

test('what counts as a connection that went away', () => {
  expect(connectionWasReset(new Error('read ECONNRESET'))).toBe(true);
  expect(connectionWasReset(new Error('socket hang up'))).toBe(true);
  expect(connectionWasReset(new Error('write EPIPE'))).toBe(true);
  expect(connectionWasReset(new Error('Timeout 30000ms exceeded'))).toBe(false);
  expect(connectionWasReset('ECONNRESET')).toBe(true);
  expect(connectionWasReset(undefined)).toBe(false);
});
