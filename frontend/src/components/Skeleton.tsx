interface SkeletonRowsProps {
  /** How many placeholder rows to draw; match the page size where one is known. */
  rows?: number;
  /**
   * `table` rows are the height of a works row, `line` the height of a one-line list row, `text` a
   * line of body copy, `title` a heading.
   */
  kind?: 'table' | 'line' | 'text' | 'title';
}

/**
 * Placeholder rows for a list that has not arrived yet.
 *
 * Holds the height the real rows will take, so the page does not paint a one-line "Loading…" and
 * then jump to a full table. Hidden from assistive technology: the page's `aria-busy` (or the
 * absence of content) already says the list is loading, and a screen reader has nothing to gain
 * from a dozen empty boxes. Callers keep their `!error &&` guard — a failed load has no rows to
 * wait for, and showing a skeleton under an error would promise ones that never come.
 */
export function SkeletonRows({ rows = 6, kind = 'table' }: SkeletonRowsProps) {
  return (
    <div className="skeleton-rows" data-kind={kind} aria-hidden="true">
      {Array.from({ length: rows }, (_, index) => (
        <span key={index} className="skeleton" />
      ))}
    </div>
  );
}
