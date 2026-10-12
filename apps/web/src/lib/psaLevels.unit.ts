import { test, expect } from '@playwright/test';
import { portalClassification, psaLevelNames, psaLevels } from './psaLevels';

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

test('the levels go by the names the PSA gives them, whether the provider arrives as its number or its name', () => {
  expect(psaLevelNames(1)).toEqual(['Type', 'Subtype', 'Item']);
  expect(psaLevelNames('ConnectWisePsa')).toEqual(['Type', 'Subtype', 'Item']);
  expect(psaLevelNames('AutotaskPsa')).toEqual(['Ticket type', 'Issue type', 'Sub-issue type']);
  expect(psaLevelNames(2)).toEqual(['Ticket type', 'Issue type', 'Sub-issue type']);
});

/**
 * What the connection's rules make of those levels. Nothing is ever shown in a rule's place: a
 * ticket the rules do not name reads "Unmapped", and a connection with no rules says nothing.
 */
const filed = { ticketType: 'Incident', issueType: 'Hardware', subIssueType: null };

test('a work type and a subcategory are listed where a rule gave them', () => {
  expect(portalClassification({ ...filed, workType: 'Break/fix', subcategory: 'Printing', mapped: true })).toEqual([
    { label: 'Work type', value: 'Break/fix' },
    { label: 'Subcategory', value: 'Printing' },
  ]);
  expect(portalClassification({ ...filed, workType: 'Break/fix', subcategory: null, mapped: true })).toEqual([
    { label: 'Work type', value: 'Break/fix' },
  ]);
});

test('a ticket no rule names says Unmapped and is given nothing else', () => {
  expect(portalClassification({ ...filed, workType: null, subcategory: null, mapped: false })).toEqual([
    { label: 'Portal classification', value: 'Unmapped' },
  ]);
});

test('a connection with no rules, an older API and a ticket filed under nothing say nothing', () => {
  expect(portalClassification({ ...filed, workType: null, subcategory: null, mapped: null })).toEqual([]);
  expect(portalClassification(filed)).toEqual([]);
  expect(portalClassification(null)).toEqual([]);
});
