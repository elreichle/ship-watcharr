import type { ReactNode } from 'react';

interface EmptyStateProps {
  /** A short title in the text face — what this empty page is, in the reader's words. */
  title: string;
  /** One sentence: why it is empty and what fills it. Links are welcome. */
  children: ReactNode;
  /** The one thing to do next, if there is one — a Link styled as a button, or a button. */
  action?: ReactNode;
}

/**
 * An empty list with presence: a heading, a sentence, and at most one action.
 *
 * The copy stays with the page that owns it — this component only gives every empty state the same
 * shape, so a reader learns once what an empty page looks like and what it asks of them.
 */
export function EmptyState({ title, children, action }: EmptyStateProps) {
  return (
    <div className="empty-state">
      <h2>{title}</h2>
      <p>{children}</p>
      {action}
    </div>
  );
}
