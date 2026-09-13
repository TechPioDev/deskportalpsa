/**
 * The PSA platforms shown across the public site — one list, used by the hero rotator, the
 * ecosystem diagram, the integration grid and the footer. Adding a platform is an entry here and
 * nothing else.
 *
 * No platform is ever labelled by status — no badge, "coming soon" or launch date (#26): every
 * platform keeps the same tile, spoke and page structure. `hasConnector` exists for one reason
 * only: to stop a page promising something that is not true yet. It changes WORDING (no "connect
 * your environment" step where there is nothing to connect) and must never be rendered as a label.
 * Flip it in the same change that ships the connector in `packages/connectors`.
 */
export type PsaPlatform = {
  id: string;
  name: string;
  /** Two letters for the tile mark. We ship no third-party logos we have no licence to use. */
  initials: string;
  /** A working connector ships in this build. Wording only — never rendered as a badge. */
  hasConnector: boolean;
};

export const PSA_PLATFORMS: PsaPlatform[] = [
  { id: 'connectwise', name: 'ConnectWise PSA', initials: 'CW', hasConnector: true },
  { id: 'autotask', name: 'Autotask PSA', initials: 'AT', hasConnector: true },
  { id: 'halo', name: 'HaloPSA', initials: 'HA', hasConnector: false },
  { id: 'kaseya-bms', name: 'Kaseya BMS', initials: 'KB', hasConnector: false },
  { id: 'syncro', name: 'Syncro', initials: 'SY', hasConnector: false },
  { id: 'superops', name: 'SuperOps', initials: 'SO', hasConnector: false },
  { id: 'n-able', name: 'N-able MSP Manager', initials: 'NA', hasConnector: false },
  { id: 'atera', name: 'Atera', initials: 'AE', hasConnector: false },
];

/** Shown on every card, so no platform reads as more or less established than another. */
export const PLATFORM_DESCRIPTOR = 'Service management integration';

/** One place decides the URL shape, so a link and a route can never disagree about it. */
export const platformHref = (p: PsaPlatform | string) =>
  `/integrations/${typeof p === 'string' ? p : p.id}`;

export const findPlatform = (id: string) => PSA_PLATFORMS.find((p) => p.id === id);
