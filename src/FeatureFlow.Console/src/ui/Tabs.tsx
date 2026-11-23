import "./ui.css";

export interface Tab<T extends string> {
  id: T;
  label: string;
  /** Shown but not selectable, with why on hover (such as "in B2"). */
  disabled?: string;
}

/** A row of tabs; the caller shows the selected tab's content under it. */
export function Tabs<T extends string>({ tabs, selected, onSelect, label }: { tabs: readonly Tab<T>[]; selected: T; onSelect: (id: T) => void; label: string }) {
  return (
    <div class="ff-tabs" role="tablist" aria-label={label}>
      {tabs.map(tab => (
        <button
          key={tab.id}
          type="button"
          role="tab"
          class="ff-tab"
          aria-selected={tab.id === selected}
          disabled={tab.disabled !== undefined}
          title={tab.disabled}
          onClick={() => onSelect(tab.id)}
        >
          {tab.label}
        </button>
      ))}
    </div>
  );
}
