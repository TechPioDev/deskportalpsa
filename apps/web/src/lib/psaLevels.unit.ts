import { test, expect } from '@playwright/test';
import { psaLevels } from './psaLevels';

/**
 * What a PSA files a ticket under, as the ticket page lists it: each PSA's own names for its three
 * levels, and only the levels the ticket has.
 */

test('ConnectWise files under a type, a subtype and an item', () => {
  expect(psaLevels(1, { ticketType: 'Hardware', issueType: 'Printer', subIssueType: 'Toner' })).toEqual([
    { label: 'Type (PSA)', value: 'Hardware' },
    { label: 'Subtype (PSA)', value: 'Printer' },
    { label: 'Item (PSA)', value: 'Toner' },
  ]);
});

test('Autotask files under a ticket type, an issue type and a sub-issue type', () => {
  expect(psaLevels(2, { ticketType: 'Incident', issueType: 'Hardware', subIssueType: 'Printer' })).toEqual([
    { label: 'Ticket type (PSA)', value: 'Incident' },
    { label: 'Issue type (PSA)', value: 'Hardware' },
    { label: 'Sub-issue type (PSA)', value: 'Printer' },
  ]);
});

test('only the levels a ticket has are listed, each under its own name', () => {
  expect(psaLevels(1, { ticketType: 'Hardware', issueType: null, subIssueType: 'Toner' })).toEqual([
    { label: 'Type (PSA)', value: 'Hardware' },
    { label: 'Item (PSA)', value: 'Toner' },
  ]);
  expect(psaLevels(2, { ticketType: null, issueType: '  ', subIssueType: null })).toEqual([]);
});

test('a ticket filed under nothing, or one that is not from a PSA, lists nothing', () => {
  expect(psaLevels(2, null)).toEqual([]);
  expect(psaLevels(null, null)).toEqual([]);
});
