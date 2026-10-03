'use client';

/**
 * The header clock, server-backed since Phase 6: what runs is a work session the API holds, not a
 * browser counter. Kept under the old names so the layout does not change; the ticket page uses
 * the work-time hook directly.
 */
export { WorkTimeProvider as TimerProvider, ActiveWorkWidget as TimerWidget } from '@/components/WorkTime';
