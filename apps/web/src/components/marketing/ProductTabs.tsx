'use client';

import { useState } from 'react';
import { Ticket, MessageSquare, Paperclip, History, Settings2, Building2 } from 'lucide-react';
import { BrowserFrame, ProductScreen, type ProductView } from '@/components/marketing/ProductUI';

const TABS: { id: ProductView; label: string; icon: typeof Ticket; url: string; note: string }[] = [
  { id: 'requests', label: 'Client portal', icon: Building2, url: 'PioManage', note: 'What your client opens: their requests, their progress, one button to raise another.' },
  { id: 'conversation', label: 'Conversation', icon: MessageSquare, url: 'Support request', note: 'One thread for client and support team. Internal notes never cross over.' },
  { id: 'files', label: 'Files', icon: Paperclip, url: 'Shared files', note: 'Screenshots and documents shared either way, attached to the request.' },
  { id: 'updates', label: 'Updates', icon: History, url: 'Recent updates', note: 'Every change, so nobody has to ask where a request stands.' },
  { id: 'sync', label: 'PSA sync', icon: Settings2, url: 'Connected PSA', note: 'What flows between the portal and whichever PSA you connect.' },
];

/**
 * Lets a visitor walk the product rather than read about it.
 *
 * The frame keeps a fixed minimum height so switching tabs cannot make the page jump — a layout
 * shift here would undo the impression the section exists to create.
 */
export function ProductTabs() {
  const [active, setActive] = useState<ProductView>('requests');
  const current = TABS.find((t) => t.id === active)!;

  return (
    <div>
      <div className="mb-5 flex snap-x gap-2 overflow-x-auto pb-1" role="tablist" aria-label="Product views">
        {TABS.map(({ id, label, icon: Icon }) => {
          const on = id === active;
          return (
            <button
              key={id}
              role="tab"
              aria-selected={on}
              onClick={() => setActive(id)}
              className={`inline-flex shrink-0 snap-start items-center gap-2 rounded-full border px-3.5 py-2 text-[13px] font-medium transition-colors ${
                on
                  ? 'border-brand bg-brand text-brand-fg'
                  : 'border-[var(--border)] bg-[var(--surface)] text-[var(--muted)] hover:text-[var(--fg)]'
              }`}
            >
              <Icon size={14} aria-hidden="true" />
              {label}
            </button>
          );
        })}
      </div>

      <BrowserFrame url={current.url}>
        <div key={active} className="dp-rise min-h-[19rem]">
          <ProductScreen view={active} />
        </div>
      </BrowserFrame>

      <p className="mt-3 text-[13px] text-[var(--muted)]">{current.note}</p>
    </div>
  );
}
