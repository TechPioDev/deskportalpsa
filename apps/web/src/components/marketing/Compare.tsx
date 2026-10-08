import { AlertTriangle, Check, Mail, Search, FileQuestion, PhoneCall, Clock3, EyeOff,
  LayoutDashboard, MessagesSquare, FolderCheck, BellRing, RefreshCw, Smile } from 'lucide-react';

const WITHOUT = [
  { icon: Search, text: 'Clients are unsure where to send a request, so it arrives three different ways.' },
  { icon: Mail, text: 'Threads fork across inboxes and nobody is certain which reply is current.' },
  { icon: PhoneCall, text: '“Any update on that ticket?” — asked by email, then again by phone.' },
  { icon: FileQuestion, text: 'Screenshots sit in an inbox instead of on the ticket.' },
  { icon: EyeOff, text: 'Clients cannot see status, priority or who is working on it.' },
  { icon: Clock3, text: 'Technicians spend the day explaining the process instead of doing the work.' },
];

const WITH = [
  { icon: LayoutDashboard, text: 'One place to raise a request, and one place to look for it afterwards.' },
  { icon: MessagesSquare, text: 'A single thread per ticket. Internal notes stay internal.' },
  { icon: BellRing, text: 'Status, priority and assignment are visible without anyone being asked.' },
  { icon: FolderCheck, text: 'Screenshots and documents land on the ticket, both directions.' },
  { icon: RefreshCw, text: 'Everything synchronises with your PSA automatically.' },
  { icon: Smile, text: 'Technicians stay in the PSA. The client experience improves anyway.' },
];

function Column({
  tone, title, caption, items,
}: {
  tone: 'problem' | 'solution';
  title: string;
  caption: string;
  items: { icon: typeof Mail; text: string }[];
}) {
  const problem = tone === 'problem';
  return (
    <div
      className={`rounded-2xl border p-6 ${
        problem
          ? 'border-[var(--border)] bg-[var(--bg)]'
          : 'border-brand/25 bg-brand-tint/60 dark:border-brand-soft/25 dark:bg-brand/15'
      }`}
    >
      <div className="mb-4 flex items-center gap-2.5">
        <span
          className={`flex h-8 w-8 items-center justify-center rounded-lg ${
            problem
              ? 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300'
              : 'bg-brand text-brand-fg'
          }`}
        >
          {problem ? <AlertTriangle size={15} aria-hidden="true" /> : <Check size={15} aria-hidden="true" />}
        </span>
        <div>
          <h3 className="text-[15px] font-semibold">{title}</h3>
          <p className="text-[12px] text-[var(--muted)]">{caption}</p>
        </div>
      </div>

      <ul className="space-y-3">
        {items.map(({ icon: Icon, text }) => (
          <li key={text} className="flex gap-2.5 text-[13.5px] leading-relaxed text-[var(--muted)]">
            <Icon
              size={15}
              aria-hidden="true"
              className={`mt-0.5 shrink-0 ${problem ? 'text-amber-600 dark:text-amber-400' : 'text-brand-mid'}`}
            />
            {text}
          </li>
        ))}
      </ul>
    </div>
  );
}

/** The same day, told twice. Side by side so the difference is structural, not asserted. */
export function Compare() {
  return (
    <div className="grid gap-5 lg:grid-cols-2">
      <Column tone="problem" title="Without PioManage" caption="Support lives in an inbox" items={WITHOUT} />
      <Column tone="solution" title="With PioManage" caption="Support lives in one place" items={WITH} />
    </div>
  );
}
