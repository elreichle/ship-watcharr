import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type CSSProperties,
  type MouseEvent,
  type TouchEvent,
} from 'react';
import { Link, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import type { Book, Chapter, Download, ReadingPosition } from '../api/types';
import { EmptyState } from '../components/EmptyState';
import { Icon } from '../components/Icon';
import { SkeletonRows } from '../components/Skeleton';
import { DOWNLOAD_STATUS_LABELS } from '../downloads';
import { useDownloads } from '../hooks/useDownloads';
import {
  READER_SETTING_RANGES,
  readReaderSettings,
  writeReaderSettings,
  type ReaderSettings,
} from '../readerSettings';

/**
 * How long the page waits after the last page turn before it writes the reader's place. Long
 * enough that someone leafing through a chapter is one write rather than one per page; short
 * enough that closing the tab a moment later loses nothing much — and the unload path below
 * flushes whatever is pending anyway.
 */
const SAVE_DELAY_MS = 3000;

/**
 * The gap between one page's column and the next, in px. Off-screen — the viewport clips to one
 * page — so its only job is to keep a line's tail from peeking in from the neighbouring page.
 */
const COLUMN_GAP_PX = 48;

/** How far a finger has to travel sideways, in px, before it is a page turn and not a wobble. */
const SWIPE_MIN_PX = 48;

/**
 * What counts as a block for remembering a place: the things a reader is "on". Descended into
 * rather than read off the chapter's top-level children, because AO3 wraps a whole chapter's text
 * in one div — a place kept as "the div" would put every reader back at the top.
 */
const BLOCK_SELECTOR = 'p, h1, h2, h3, h4, h5, h6, blockquote, hr, ul, ol, dl, pre, table, figure, img';

/** The chapter's blocks in document order, which is the order a block index counts in. */
function blocksOf(chapter: HTMLDivElement | null): HTMLElement[] {
  return chapter === null ? [] : Array.from(chapter.querySelectorAll<HTMLElement>(BLOCK_SELECTOR));
}

/**
 * Reading a work's EPUB in the app.
 *
 * The book behind this page is the reader's own EPUB download of the work, found in the same queue
 * the Downloads page shows. No download, or one with nothing readable behind it yet, and this is
 * the page that asks for one: the hook's polling then carries it into the reader when the file
 * lands, so "fetch, then read" is one page rather than a trip through the queue and back.
 */
export function ReaderPage() {
  const { workId: workIdParam } = useParams();
  const workId = Number(workIdParam);

  const { downloads, error, request } = useDownloads();

  const epub = downloads?.find((d) => d.workId === workId && d.format === 'Epub') ?? null;
  // Readable on the same terms the Downloads page offers "Save": the request's own file once it
  // is complete, or the earlier copy it is still holding while a newer version is fetched.
  const readable = epub !== null && (epub.status === 'Complete' || epub.previousSizeBytes !== null);

  if (downloads === null) {
    return (
      <div className="page reader-page">
        {error !== null ? (
          <p className="error" role="alert">
            {error}
          </p>
        ) : (
          <SkeletonRows rows={4} kind="line" />
        )}
      </div>
    );
  }

  if (!readable) {
    return (
      <div className="page reader-page">
        <BackToWork workId={workId} />
        <NoBookYet
          workId={workId}
          download={epub}
          error={error}
          onFetch={() => void request(workId, 'Epub')}
        />
      </div>
    );
  }

  // Keyed by request, so a request that finishes fetching a newer version — which changes the
  // book at the same address — reopens rather than repainting one book's chapters under another's.
  return <Reader key={epub.id} workId={workId} download={epub} />;
}

function BackToWork({ workId }: { workId: number }) {
  return (
    <Link className="back-link" to={`/works/${workId}`}>
      <Icon name="chevron-right" size="xs" className="back-link-icon" />
      Back to the work
    </Link>
  );
}

/** The page before there is a book: no EPUB asked for, or one asked for and not here yet. */
function NoBookYet({
  download,
  error,
  onFetch,
}: {
  workId: number;
  download: Download | null;
  error: string | null;
  onFetch: () => void;
}) {
  const inFlight = download !== null && (download.status === 'Pending' || download.status === 'Downloading');

  return (
    <>
      <EmptyState
        title={download === null ? 'No EPUB yet' : DOWNLOAD_STATUS_LABELS[download.status]}
        action={
          inFlight ? undefined : (
            <button type="button" className="button" onClick={onFetch}>
              {download === null ? 'Fetch the EPUB' : 'Try again'}
            </button>
          )
        }
      >
        {download === null
          ? 'Reading here needs a copy of the work as EPUB, fetched from AO3 and kept on this server. It joins the same queue every download does, so it arrives shortly after it is asked for.'
          : inFlight
            ? 'The EPUB is on its way. It waits its turn behind the same rate limit the scraper uses, and this page opens it the moment it lands.'
            : (download.errorMessage ?? 'The fetch failed.')}
      </EmptyState>
      {error !== null && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </>
  );
}

/** Where a reader is, in the terms the server keeps: a chapter, and a block within it. */
interface Place {
  chapterIndex: number;
  blockIndex: number;
}

/**
 * The page a reader is on, in terms that survive a reflow: the block the page begins with, and
 * how many pages into that block the page is. The offset is nearly always 0 — a page opens with
 * the start of a paragraph — and is what keeps the fourth page of a very long paragraph from
 * snapping back to its first when the window is resized. The server is told only the block.
 */
interface Anchor {
  blockIndex: number;
  pageOffset: number;
}

/** Which page to show once a chapter's markup is on the page. */
type PageTarget = { kind: 'anchor'; anchor: Anchor } | { kind: 'last' };

const CHAPTER_START: PageTarget = { kind: 'anchor', anchor: { blockIndex: 0, pageOffset: 0 } };

/** What the current chapter's layout came to: how far one page is from the next, and how many. */
interface Geometry {
  stride: number;
  count: number;
}

/** The book, open. */
function Reader({ workId, download }: { workId: number; download: Download }) {
  const [book, setBook] = useState<Book | null>(null);
  const [chapter, setChapter] = useState<Chapter | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [settings, setSettings] = useState<ReaderSettings>(readReaderSettings);

  // The chapter being shown, and the page of it the next layout should land on. The target is
  // read by the layout effect once the chapter's markup is on the page; every later layout of the
  // same chapter — a resize, a change of text size — keeps the anchor instead.
  const [chapterIndex, setChapterIndex] = useState<number | null>(null);
  const pendingTarget = useRef<PageTarget>(CHAPTER_START);

  // The page being shown and how many there are, for the controls. The refs beside them are the
  // same facts for the handlers, which would otherwise close over a stale render.
  const [pages, setPages] = useState({ page: 0, count: 1 });
  const pageRef = useRef(0);
  const geometry = useRef<Geometry>({ stride: 1, count: 1 });
  const anchor = useRef<Anchor>({ blockIndex: 0, pageOffset: 0 });

  // What the server was last told, and what the page currently holds. Compared before every save
  // so that a page sitting still writes nothing.
  const savedPlace = useRef<Place | null>(null);
  const currentPlace = useRef<Place>({ chapterIndex: 0, blockIndex: 0 });
  // Whether there is a place to save at all. Until the book and the stored place have both
  // arrived, the page holds a placeholder, and a save on the way out — which React's development
  // mode triggers by mounting effects twice — would write the placeholder over the real place.
  const bookOpened = useRef(false);
  const saveTimer = useRef<number | undefined>(undefined);

  // The stage is the room the pages have; the viewport is the one page of it that shows, sized to
  // whole pixels; the columns are the chapter laid out sideways, one column per page, slid along
  // behind the viewport; the chapter is the author's markup, whose blocks the place counts.
  const stageRef = useRef<HTMLDivElement | null>(null);
  const viewportRef = useRef<HTMLDivElement | null>(null);
  const columnsRef = useRef<HTMLDivElement | null>(null);
  const chapterRef = useRef<HTMLDivElement | null>(null);

  const chapterCount = book?.chapters.length ?? 0;

  // ---- opening ---------------------------------------------------------------------------------

  useEffect(() => {
    let current = true;

    const open = async () => {
      try {
        // The book and the reader's place, together: neither is any use without the other, and
        // waiting for one before asking for the second is a round trip for nothing.
        const [opened, position] = await Promise.all([
          api.getBook(download.id),
          api.getReadingPosition(workId),
        ]);
        if (!current) return;

        const start = startingPlace(opened, position);
        savedPlace.current = position === undefined ? null : start;
        currentPlace.current = start;
        pendingTarget.current = { kind: 'anchor', anchor: { blockIndex: start.blockIndex, pageOffset: 0 } };
        bookOpened.current = true;

        setBook(opened);
        setChapterIndex(start.chapterIndex);
      } catch (err) {
        if (current) setError(err instanceof ApiError ? err.message : 'Could not open this book.');
      }
    };

    void open();

    return () => {
      current = false;
    };
  }, [download.id, workId]);

  // ---- one chapter at a time --------------------------------------------------------------------

  useEffect(() => {
    if (chapterIndex === null) return;

    let current = true;
    setChapter(null);

    api
      .getChapter(download.id, chapterIndex)
      .then((loaded) => {
        if (current) setChapter(loaded);
      })
      .catch((err: unknown) => {
        if (current) setError(err instanceof ApiError ? err.message : 'Could not load this chapter.');
      });

    return () => {
      current = false;
    };
  }, [download.id, chapterIndex]);

  // ---- remembering the place -------------------------------------------------------------------

  const save = useCallback(() => {
    window.clearTimeout(saveTimer.current);
    if (!bookOpened.current) return;

    const place = currentPlace.current;
    const saved = savedPlace.current;
    if (saved !== null && saved.chapterIndex === place.chapterIndex && saved.blockIndex === place.blockIndex) {
      return;
    }

    // Marked saved before the request answers: a save that fails leaves the next page turn to try
    // again, and two saves in flight for one place is what this ref exists to stop.
    savedPlace.current = place;

    const { count } = geometry.current;
    const progress = chapterCount === 0 ? 0 : (place.chapterIndex + pageRef.current / count) / chapterCount;

    api
      .setReadingPosition(workId, { ...place, progress: Math.min(1, Math.max(0, progress)) })
      .catch(() => {
        // Not surfaced: a place that failed to save is retried by the next page turn, and an error
        // banner over the text for a background write is worse than the retry.
        savedPlace.current = saved;
      });
  }, [chapterCount, workId]);

  const scheduleSave = useCallback(() => {
    window.clearTimeout(saveTimer.current);
    saveTimer.current = window.setTimeout(save, SAVE_DELAY_MS);
  }, [save]);

  // Leaving — the sidebar, the back link, the tab closing — flushes whatever is pending. The
  // request is sent with keepalive, so it outlives the page it was sent from.
  useEffect(() => {
    window.addEventListener('pagehide', save);
    return () => {
      window.removeEventListener('pagehide', save);
      save();
    };
  }, [save]);

  // ---- pages -----------------------------------------------------------------------------------

  /**
   * Slides the columns so that one page shows, and makes that page the reader's place. Nothing is
   * measured afresh: the layout is whatever the last call to `layout` left.
   */
  const showPage = useCallback(
    (page: number) => {
      const columns = columnsRef.current;
      if (columns === null) return;

      const { stride, count } = geometry.current;
      const shown = Math.min(count - 1, Math.max(0, page));
      columns.style.transform = `translateX(-${shown * stride}px)`;

      anchor.current = anchorOfPage(blocksOf(chapterRef.current), columns, shown, stride);
      pageRef.current = shown;
      setPages({ page: shown, count });

      currentPlace.current = { ...currentPlace.current, blockIndex: anchor.current.blockIndex };
      scheduleSave();
    },
    [scheduleSave],
  );

  /**
   * Lays the chapter out as pages the size of the stage, and shows the page the target asks for.
   * Called when a chapter's markup arrives and again whenever the stage or the type changes,
   * because either one moves every page boundary.
   */
  const layout = useCallback(
    (target: PageTarget) => {
      const stage = stageRef.current;
      const viewport = viewportRef.current;
      const columns = columnsRef.current;
      if (stage === null || viewport === null || columns === null) return;

      // Whole pixels for the page and the column both, so that the twentieth page is exactly
      // twenty strides along and not twenty strides and a smear of subpixel drift.
      const room = stage.getBoundingClientRect();
      const width = Math.floor(room.width);
      const height = Math.floor(room.height);
      if (width <= 0 || height <= 0) return;

      viewport.style.width = `${width}px`;
      viewport.style.height = `${height}px`;
      viewport.style.setProperty('--reader-page-height', `${height}px`);
      columns.style.columnWidth = `${width}px`;

      // The slide has to come off before measuring: a translated element gives up that much of
      // its overflow, and the page count would come out short by however far in the reader was.
      columns.style.transform = 'none';
      viewport.scrollLeft = 0;
      viewport.scrollTop = 0;

      const stride = width + COLUMN_GAP_PX;
      const count = Math.max(1, Math.round((viewport.scrollWidth - width) / stride) + 1);
      geometry.current = { stride, count };

      let page: number;
      if (target.kind === 'last') {
        page = count - 1;
      } else {
        const block = blocksOf(chapterRef.current)[target.anchor.blockIndex];
        page = block === undefined ? 0 : pageOfBlock(block, columns, stride) + target.anchor.pageOffset;
        // The offset was measured in a layout with a different page height; the block may no
        // longer reach that far.
        if (block !== undefined) page = Math.min(page, lastPageOfBlock(block, columns, stride));
      }

      showPage(page);
    },
    [showPage],
  );

  // Once the chapter's markup is on the page, lay it out and put the reader where they were.
  // Layout rather than plain effect so the jump lands before paint — a chapter that flashes its
  // opening lines before sliding to the middle reads as a page that lost its place.
  useLayoutEffect(() => {
    if (chapter === null) return;
    layout(pendingTarget.current);
  }, [chapter, layout]);

  // The stage changing size — a window resized, the sidebar railed, a phone turned — moves every
  // page boundary; the reader keeps their block and the page is found again around it. Observed
  // per chapter because the stage is remounted with the skeleton between chapters.
  useEffect(() => {
    const stage = stageRef.current;
    if (chapter === null || stage === null) return;

    const observer = new ResizeObserver(() => {
      layout({ kind: 'anchor', anchor: anchor.current });
    });
    observer.observe(stage);

    return () => observer.disconnect();
  }, [chapter, layout]);

  // ---- moving ----------------------------------------------------------------------------------

  const goTo = useCallback(
    (index: number, target: PageTarget = CHAPTER_START) => {
      if (index < 0 || index >= chapterCount || index === chapterIndex) return;

      pendingTarget.current = target;
      currentPlace.current = {
        chapterIndex: index,
        blockIndex: target.kind === 'anchor' ? target.anchor.blockIndex : 0,
      };
      setChapterIndex(index);
      // A chapter change is worth writing at once: it is a deliberate move, not a scroll.
      scheduleSave();
    },
    [chapterCount, chapterIndex, scheduleSave],
  );

  /**
   * One page on or back. Off the end of a chapter is the start of the next; off the start is the
   * last page of the one before, so the book reads as one run of pages with the chapters in it.
   */
  const turn = useCallback(
    (direction: 1 | -1) => {
      if (chapterIndex === null || chapter === null) return;

      const next = pageRef.current + direction;
      if (next >= 0 && next < geometry.current.count) {
        showPage(next);
      } else if (direction === 1) {
        goTo(chapterIndex + 1);
      } else {
        goTo(chapterIndex - 1, { kind: 'last' });
      }
    },
    [chapter, chapterIndex, goTo, showPage],
  );

  useEffect(() => {
    if (chapterIndex === null) return;

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.altKey || event.ctrlKey || event.metaKey) return;
      // Not while the reader is typing or in the chapter picker, whose arrows mean something else.
      const target = event.target instanceof HTMLElement ? event.target : null;
      if (target?.closest('input, select, textarea, [contenteditable]')) return;

      switch (event.key) {
        case 'ArrowRight':
        case 'PageDown':
          turn(1);
          break;
        case 'ArrowLeft':
        case 'PageUp':
          turn(-1);
          break;
        case ' ':
          // Space pages down in a browser, so it pages on here — and back with shift, the same
          // as everywhere else. Not on a focused button, where space is the click: the reader
          // who just pressed Next has it focused, and one press would turn two pages.
          if (target?.closest('button, a, summary')) return;
          event.preventDefault();
          turn(event.shiftKey ? -1 : 1);
          break;
        default:
          return;
      }
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [chapterIndex, turn]);

  // A swipe across the page turns it. Only the end of the gesture is looked at, and only when it
  // travelled mostly sideways: a finger that drifted while scrolling nothing is not a page turn.
  const touchStart = useRef<{ x: number; y: number } | null>(null);

  const onTouchStart = (event: TouchEvent) => {
    const touch = event.changedTouches[0];
    touchStart.current = touch === undefined ? null : { x: touch.clientX, y: touch.clientY };
  };

  const onTouchEnd = (event: TouchEvent) => {
    const start = touchStart.current;
    const touch = event.changedTouches[0];
    touchStart.current = null;
    if (start === null || touch === undefined) return;

    const dx = touch.clientX - start.x;
    const dy = touch.clientY - start.y;
    if (Math.abs(dx) < SWIPE_MIN_PX || Math.abs(dx) < Math.abs(dy) * 1.5) return;

    turn(dx < 0 ? 1 : -1);
  };

  // A link within the chapter to a footnote or a heading would have the browser scroll the
  // viewport to it, which is the one thing a paged viewport must never do. It turns to the page
  // the target is on instead. Links out to the web are left to the browser.
  const onChapterClick = (event: MouseEvent<HTMLDivElement>) => {
    if (!(event.target instanceof Element)) return;
    const link = event.target.closest<HTMLAnchorElement>('a[href^="#"]');
    const columns = columnsRef.current;
    if (link === null || columns === null) return;

    event.preventDefault();
    let id: string;
    try {
      id = decodeURIComponent(link.getAttribute('href')?.slice(1) ?? '');
    } catch {
      return;
    }
    if (id === '') return;

    const target = columns.querySelector<HTMLElement>(`[id="${CSS.escape(id)}"]`);
    if (target !== null) showPage(pageOfBlock(target, columns, geometry.current.stride));
  };

  // ---- text settings ---------------------------------------------------------------------------

  const updateSettings = (patch: Partial<ReaderSettings>) => {
    setSettings((current) => {
      const next = { ...current, ...patch };
      writeReaderSettings(next);
      return next;
    });
  };

  // Bigger text reflows the chapter into more pages, and the page the reader was on is now a
  // different stretch of text. The place is a block, so the page is found again around the block
  // — which is the whole reason the place is kept as one.
  const settingsApplied = useRef(settings);
  useLayoutEffect(() => {
    if (settingsApplied.current === settings) return;
    settingsApplied.current = settings;
    layout({ kind: 'anchor', anchor: anchor.current });
  }, [layout, settings]);

  // ---- render ----------------------------------------------------------------------------------

  if (error !== null) {
    return (
      <div className="page reader-page">
        <BackToWork workId={workId} />
        <p className="error" role="alert">
          {error}
        </p>
      </div>
    );
  }

  if (book === null || chapterIndex === null) {
    return (
      <div className="page reader-page">
        <SkeletonRows rows={6} kind="line" />
      </div>
    );
  }

  const heading = book.chapters[chapterIndex];
  // AO3 opens every chapter with its own heading, and a second one above it in the same words is
  // a page that says everything twice. The page's heading is for a chapter that has none.
  const chapterHasHeading = chapter !== null && /^\s*<h[1-3][\s>]/i.test(chapter.html);

  const atStart = chapterIndex === 0 && pages.page === 0;
  const atEnd = chapter !== null && chapterIndex === chapterCount - 1 && pages.page === pages.count - 1;

  // The reader's own settings, as custom properties the stylesheet reads. An inline style
  // outranks the theme, which is right here and nowhere else in the app: these three numbers
  // are the reader's, chosen for this screen, and a theme has no business overruling them.
  const textStyle = {
    '--reader-font-size': `${settings.fontSize}px`,
    '--reader-measure': `${settings.measure}ch`,
    '--reader-line-height': String(settings.lineHeight),
  } as CSSProperties;

  return (
    <div className="page reader-page" style={textStyle}>
      <header className="reader-bar">
        <div className="reader-bar-row">
          <BackToWork workId={workId} />
          <span className="reader-bar-title">{book.title}</span>
          {book.isEarlierCopy && (
            <span className="hint" title="A newer version of the work is being fetched. This is the copy you already had.">
              earlier copy
            </span>
          )}
        </div>

        <div className="reader-bar-row reader-nav">
          <button
            type="button"
            className="clickable-icon"
            onClick={() => goTo(chapterIndex - 1)}
            disabled={chapterIndex === 0}
            aria-label="Previous chapter"
            title="Previous chapter"
          >
            <Icon name="chevron-right" size="s" className="reader-step-back" />
          </button>

          <label className="reader-toc">
            <span className="visually-hidden">Chapter</span>
            <select value={chapterIndex} onChange={(event) => goTo(Number(event.target.value))}>
              {book.chapters.map((entry) => (
                <option key={entry.index} value={entry.index}>
                  {entry.title}
                </option>
              ))}
            </select>
          </label>

          <span className="hint reader-count">
            {chapterIndex + 1} of {chapterCount}
          </span>

          <button
            type="button"
            className="clickable-icon"
            onClick={() => goTo(chapterIndex + 1)}
            disabled={chapterIndex >= chapterCount - 1}
            aria-label="Next chapter"
            title="Next chapter"
          >
            <Icon name="chevron-right" size="s" />
          </button>

          <details className="reader-settings">
            <summary aria-label="Text settings">Aa</summary>
            <div className="reader-settings-panel">
              <label>
                Size
                <input
                  type="range"
                  {...READER_SETTING_RANGES.fontSize}
                  value={settings.fontSize}
                  onChange={(event) => updateSettings({ fontSize: Number(event.target.value) })}
                />
              </label>
              <label>
                Width
                <input
                  type="range"
                  {...READER_SETTING_RANGES.measure}
                  value={settings.measure}
                  onChange={(event) => updateSettings({ measure: Number(event.target.value) })}
                />
              </label>
              <label>
                Spacing
                <input
                  type="range"
                  {...READER_SETTING_RANGES.lineHeight}
                  value={settings.lineHeight}
                  onChange={(event) => updateSettings({ lineHeight: Number(event.target.value) })}
                />
              </label>
            </div>
          </details>
        </div>
      </header>

      <article className="reader-body">
        <div ref={stageRef} className="reader-stage">
          {chapter === null ? (
            <SkeletonRows rows={8} kind="line" />
          ) : (
            <div ref={viewportRef} className="reader-pages" onTouchStart={onTouchStart} onTouchEnd={onTouchEnd}>
              <div ref={columnsRef} className="reader-columns">
                {!chapterHasHeading && <h1 className="reader-chapter-title">{heading?.title}</h1>}

                {/* Sanitized by the server before it was ever sent — an allowlist of elements and
                    three checked attributes (a link's address, an image's source and caption, an
                    id for a footnote) — so what arrives here has nowhere left to carry script.
                    `Chapter` names the field for that; the archive's own markup reaches no
                    client. */}
                <div
                  ref={chapterRef}
                  className="reader-chapter"
                  onClick={onChapterClick}
                  dangerouslySetInnerHTML={{ __html: chapter.html }}
                />
              </div>
            </div>
          )}
        </div>

        <nav className="reader-foot" aria-label="Pages">
          <button type="button" onClick={() => turn(-1)} disabled={chapter === null || atStart}>
            Previous
          </button>

          <span className="hint reader-page-count" aria-live="polite">
            {chapter !== null && `Page ${pages.page + 1} of ${pages.count}`}
          </span>

          {atEnd ? (
            <Link className="button" to={`/works/${workId}`}>
              The end — back to the work
            </Link>
          ) : (
            <button type="button" onClick={() => turn(1)} disabled={chapter === null}>
              Next
            </button>
          )}
        </nav>
      </article>
    </div>
  );
}

/**
 * Where to open: the place the server remembers, held to the book it is being applied to. A
 * chapter index past the end — the work shrank, or the copy is a different version — opens the
 * last chapter rather than nothing.
 */
function startingPlace(book: Book, position: ReadingPosition | undefined): Place {
  if (position === undefined || book.chapters.length === 0) return { chapterIndex: 0, blockIndex: 0 };

  const chapterIndex = Math.min(position.chapterIndex, book.chapters.length - 1);
  return { chapterIndex, blockIndex: chapterIndex === position.chapterIndex ? position.blockIndex : 0 };
}

/**
 * How far a block's left edge is from the columns' own, in the columns' coordinates. Both rects
 * carry the same slide, so it cancels: the answer is the same whichever page is showing. A block
 * that runs across several pages reports the union of its pieces, so its left edge is where it
 * starts and its right edge where it ends.
 */
function offsetOf(block: Element, columns: HTMLElement): { left: number; right: number } {
  const origin = columns.getBoundingClientRect().left;
  const rect = block.getBoundingClientRect();
  return { left: rect.left - origin, right: rect.right - origin };
}

/** The page a block starts on. Floored, so a centred rule a third of the way across still counts. */
function pageOfBlock(block: Element, columns: HTMLElement, stride: number): number {
  return Math.floor((offsetOf(block, columns).left + 1) / stride);
}

/** The page a block ends on. */
function lastPageOfBlock(block: Element, columns: HTMLElement, stride: number): number {
  return Math.max(0, Math.floor((offsetOf(block, columns).right - 1) / stride));
}

/**
 * The place a page is remembered by: the first block that starts on it, or — for a page that lies
 * wholly inside one long block — that block, and how many pages into it this page is.
 */
function anchorOfPage(blocks: HTMLElement[], columns: HTMLElement, page: number, stride: number): Anchor {
  const pageLeft = page * stride;
  let covering = 0;

  for (let index = 0; index < blocks.length; index++) {
    const { left } = offsetOf(blocks[index], columns);
    if (left >= pageLeft - 1) {
      if (left < pageLeft + stride - 1) return { blockIndex: index, pageOffset: 0 };
      break;
    }
    covering = index;
  }

  const coveringBlock = blocks[covering];
  const pageOffset = coveringBlock === undefined ? 0 : page - pageOfBlock(coveringBlock, columns, stride);
  return { blockIndex: covering, pageOffset: Math.max(0, pageOffset) };
}
