import { useCallback, useEffect, useLayoutEffect, useRef, useState, type CSSProperties } from 'react';
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
 * How long the page waits after the reader stops scrolling before it writes their place. Long
 * enough that a page being read is not a stream of writes; short enough that closing the tab a
 * moment later loses nothing much — and the unload path below flushes whatever is pending anyway.
 */
const SAVE_DELAY_MS = 3000;

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

/** The book, open. */
function Reader({ workId, download }: { workId: number; download: Download }) {
  const [book, setBook] = useState<Book | null>(null);
  const [chapter, setChapter] = useState<Chapter | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [settings, setSettings] = useState<ReaderSettings>(readReaderSettings);

  // The chapter being shown, and the block to scroll to once its markup is on the page. The block
  // is consumed by the layout effect below and then cleared, so a later scroll within the chapter
  // is the reader's and not the restore's.
  const [chapterIndex, setChapterIndex] = useState<number | null>(null);
  const pendingBlock = useRef<number>(0);

  // What the server was last told, and what the page currently holds. Compared before every save
  // so that a page sitting still writes nothing.
  const savedPlace = useRef<Place | null>(null);
  const currentPlace = useRef<Place>({ chapterIndex: 0, blockIndex: 0 });
  // Whether there is a place to save at all. Until the book and the stored place have both
  // arrived, the page holds a placeholder, and a save on the way out — which React's development
  // mode triggers by mounting effects twice — would write the placeholder over the real place.
  const bookOpened = useRef(false);
  const saveTimer = useRef<number | undefined>(undefined);

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
        pendingBlock.current = start.blockIndex;
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

  // Once the chapter's markup is on the page, put the reader where they were. Layout rather than
  // plain effect so the jump lands before paint — a chapter that flashes its opening lines before
  // scrolling to the middle reads as a page that lost its place.
  useLayoutEffect(() => {
    if (chapter === null) return;

    const main = scrollParent();
    const block = pendingBlock.current;
    pendingBlock.current = 0;

    const target = block > 0 ? blocksOf(chapterRef.current)[block] : undefined;
    if (target !== undefined) {
      target.scrollIntoView({ block: 'start' });
    } else if (main !== null) {
      main.scrollTop = 0;
    }
  }, [chapter]);

  // ---- remembering the place -------------------------------------------------------------------

  const save = useCallback(() => {
    window.clearTimeout(saveTimer.current);
    if (!bookOpened.current) return;

    const place = currentPlace.current;
    const saved = savedPlace.current;
    if (saved !== null && saved.chapterIndex === place.chapterIndex && saved.blockIndex === place.blockIndex) {
      return;
    }

    // Marked saved before the request answers: a save that fails leaves the next scroll to try
    // again, and two saves in flight for one place is what this ref exists to stop.
    savedPlace.current = place;

    const blocks = blocksOf(chapterRef.current).length;
    const progress = chapterCount === 0 ? 0 : (place.chapterIndex + (blocks === 0 ? 0 : place.blockIndex / blocks)) / chapterCount;

    api
      .setReadingPosition(workId, { ...place, progress: Math.min(1, Math.max(0, progress)) })
      .catch(() => {
        // Not surfaced: a place that failed to save is retried by the next scroll, and an error
        // banner over the text for a background write is worse than the retry.
        savedPlace.current = saved;
      });
  }, [chapterCount, workId]);

  const scheduleSave = useCallback(() => {
    window.clearTimeout(saveTimer.current);
    saveTimer.current = window.setTimeout(save, SAVE_DELAY_MS);
  }, [save]);

  // The block at the top of the view, re-read as the reader scrolls. Throttled to a frame: scroll
  // events come faster than layout can be asked about.
  useEffect(() => {
    if (chapter === null || chapterIndex === null) return;

    const main = scrollParent();
    if (main === null) return;

    let frame: number | undefined;

    const onScroll = () => {
      if (frame !== undefined) return;
      frame = window.requestAnimationFrame(() => {
        frame = undefined;
        const blockIndex = topBlock(chapterRef.current, main);
        if (blockIndex === null) return;

        currentPlace.current = { chapterIndex, blockIndex };
        scheduleSave();
      });
    };

    main.addEventListener('scroll', onScroll, { passive: true });

    return () => {
      main.removeEventListener('scroll', onScroll);
      if (frame !== undefined) window.cancelAnimationFrame(frame);
    };
  }, [chapter, chapterIndex, scheduleSave]);

  // Leaving — the sidebar, the back link, the tab closing — flushes whatever is pending. The
  // request is sent with keepalive, so it outlives the page it was sent from.
  useEffect(() => {
    window.addEventListener('pagehide', save);
    return () => {
      window.removeEventListener('pagehide', save);
      save();
    };
  }, [save]);

  // ---- moving between chapters -----------------------------------------------------------------

  const goTo = useCallback(
    (index: number) => {
      if (index < 0 || index >= chapterCount || index === chapterIndex) return;

      pendingBlock.current = 0;
      currentPlace.current = { chapterIndex: index, blockIndex: 0 };
      setChapterIndex(index);
      // A chapter change is worth writing at once: it is a deliberate move, not a scroll.
      scheduleSave();
    },
    [chapterCount, chapterIndex, scheduleSave],
  );

  useEffect(() => {
    if (chapterIndex === null) return;

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.altKey || event.ctrlKey || event.metaKey) return;
      // Not while the reader is typing or in the chapter picker, whose arrows mean something else.
      const target = event.target;
      if (target instanceof HTMLElement && target.closest('input, select, textarea, [contenteditable]')) return;

      if (event.key === 'ArrowLeft') goTo(chapterIndex - 1);
      else if (event.key === 'ArrowRight') goTo(chapterIndex + 1);
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [chapterIndex, goTo]);

  // ---- text settings ---------------------------------------------------------------------------

  const updateSettings = (patch: Partial<ReaderSettings>) => {
    setSettings((current) => {
      const next = { ...current, ...patch };
      writeReaderSettings(next);
      return next;
    });
  };

  // Bigger text reflows the chapter, and a scroll offset kept through a reflow lands on a
  // different paragraph. The place is a block, so the block is put back at the top — which is the
  // whole reason the place is kept as one.
  const settingsApplied = useRef(settings);
  useLayoutEffect(() => {
    if (settingsApplied.current === settings) return;
    settingsApplied.current = settings;

    const target = blocksOf(chapterRef.current)[currentPlace.current.blockIndex];
    target?.scrollIntoView({ block: 'start' });
  }, [settings]);

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
          <button type="button" onClick={() => goTo(chapterIndex - 1)} disabled={chapterIndex === 0}>
            Previous
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
            onClick={() => goTo(chapterIndex + 1)}
            disabled={chapterIndex >= chapterCount - 1}
          >
            Next
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
        {!chapterHasHeading && <h1 className="reader-chapter-title">{heading?.title}</h1>}

        {chapter === null ? (
          <SkeletonRows rows={8} kind="line" />
        ) : (
          // Sanitized by the server before it was ever sent — an allowlist of elements and three
          // checked attributes (a link's address, an image's source and caption, an id for a
          // footnote) — so what arrives here has nowhere left to carry script. `Chapter` names
          // the field for that; the archive's own markup reaches no client.
          <div ref={chapterRef} className="reader-chapter" dangerouslySetInnerHTML={{ __html: chapter.html }} />
        )}

        {chapter !== null && (
          <nav className="reader-foot">
            <button type="button" onClick={() => goTo(chapterIndex - 1)} disabled={chapterIndex === 0}>
              Previous chapter
            </button>
            {chapterIndex < chapterCount - 1 ? (
              <button type="button" onClick={() => goTo(chapterIndex + 1)}>
                Next chapter
              </button>
            ) : (
              <Link className="button" to={`/works/${workId}`}>
                The end — back to the work
              </Link>
            )}
          </nav>
        )}
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

/** The element every signed-in page scrolls inside. See AppLayout. */
function scrollParent(): HTMLElement | null {
  return document.getElementById('main');
}

/**
 * The index of the chapter's first block still in view — the one a reader would say they are on.
 * Null while there is nothing to measure.
 */
function topBlock(chapter: HTMLDivElement | null, main: HTMLElement): number | null {
  if (chapter === null) return null;

  const blocks = blocksOf(chapter);
  if (blocks.length === 0) return null;

  // The sticky bar covers the top of the scroll area, so "in view" starts below it.
  const bar = main.querySelector('.reader-bar');
  const top = (bar?.getBoundingClientRect().bottom ?? main.getBoundingClientRect().top) + 1;

  for (let index = 0; index < blocks.length; index++) {
    if (blocks[index].getBoundingClientRect().bottom > top) return index;
  }

  return blocks.length - 1;
}
