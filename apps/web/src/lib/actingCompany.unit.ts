import { test, expect } from '@playwright/test';
import { readActingCompany } from './actingCompany';

/**
 * Which company a client user chose, as it is read from the cookie before it is forwarded. Only
 * something that reads as one id is forwarded at all; what the id is allowed to mean is decided
 * by the API, against the companies the person has been given.
 */
const id = '7b1f6c1e-52a5-4c1b-9d0a-3f1f0c9a2e11';

test('the chosen company is read from among the other cookies', () => {
  expect(readActingCompany(`desk_at=abc; desk_company=${id}; theme=dark`)).toBe(id);
  expect(readActingCompany(`desk_company=${id.toUpperCase()}`)).toBe(id);
});

test('no cookie, or none of that name, is no company chosen', () => {
  expect(readActingCompany(null)).toBeNull();
  expect(readActingCompany('')).toBeNull();
  expect(readActingCompany('desk_at=abc; other_company=' + id)).toBeNull();
});

test('anything that is not one id is not forwarded', () => {
  for (const value of ['bolt', '', `${id},${id}`, `${id}%0d%0aX-Injected: 1`, '00000000', "' OR 1=1 --"])
    expect(readActingCompany(`desk_company=${encodeURIComponent(value)}`)).toBeNull();
});
