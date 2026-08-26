import { Link } from 'react-router-dom';
import { api } from '../api/client';
import { DOWNLOAD_FORMATS } from '../api/types';
import { DOWNLOAD_STATUS_LABELS, formatSize } from '../downloads';
import { useDownloads } from '../hooks/useDownloads';

/**
 * Asking for one work as a file, and what has come of asking so far.
 *
 * The queue this reads is the whole of the reader's own queue, narrowed here to one work — there
 * is no per-work endpoint, and inventing one would be a second scoping rule to keep honest for the
 * sake of a list that is a handful of rows. The polling that keeps it current lives in the hook,
 * so this component never learns that a fetch takes a while.
 */
export function WorkDownloads({ workId }: { workId: number }) {
  const { downloads, error, request, remove } = useDownloads();

  // Null while the queue is still loading, which is what stops the buttons claiming a work has
  // been asked for nothing before anyone knows.
  const mine = downloads?.filter((download) => download.workId === workId) ?? null;

  return (
    <section className="work-detail-section">
      <h2>Download</h2>

      <p className="hint">
        A copy fetched from AO3 and kept on this server. It joins a queue behind the same rate limit
        the scraper uses, so it arrives shortly after it is asked for rather than at once — the{' '}
        <Link to="/downloads">Downloads</Link> page lists every request you have made.
      </p>

      <div className="button-row download-formats">
        {DOWNLOAD_FORMATS.map((format) => (
          <button
            key={format}
            type="button"
            // No guard against a second click: asking again for something already queued answers
            // with the request that already exists rather than queueing a second fetch.
            onClick={() => void request(workId, format)}
          >
            {format.toUpperCase()}
          </button>
        ))}
      </div>

      {error !== null && <p className="error">{error}</p>}

      {mine !== null && mine.length > 0 && (
        <ul className="work-downloads">
          {mine.map((download) => (
            <li key={download.id}>
              <span className="work-download-format">{download.format.toUpperCase()}</span>
              <span className="download-status" data-status={download.status}>
                {DOWNLOAD_STATUS_LABELS[download.status]}
              </span>
              {download.status === 'Complete' && (
                <>
                  <span className="hint">{formatSize(download.sizeBytes)}</span>
                  {/* A plain link, so the browser's own download machinery streams the file to
                      disk rather than this page holding a whole PDF in memory to hand it back. */}
                  <a href={api.downloadFileUrl(download.id)} download>
                    Save
                  </a>
                </>
              )}
              <button type="button" className="link" onClick={() => void remove(download.id)}>
                Remove
              </button>
              {/* Which half of the fetch failed — the work's page, or the file itself — is the
                  difference between "AO3 has taken this work down" and "try again later". */}
              {download.errorMessage !== null && (
                <span className="download-error">{download.errorMessage}</span>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
