import { Link } from 'react-router-dom';
import { api } from '../api/client';
import { EmptyState } from '../components/EmptyState';
import { SkeletonRows } from '../components/Skeleton';
import { DOWNLOAD_STATUS_LABELS, formatSize } from '../downloads';
import { formatDateTimeOrDash as formatDate } from '../format';
import { useDownloads } from '../hooks/useDownloads';

export function DownloadsPage() {
  const { downloads, error, request, remove } = useDownloads();

  return (
    <div className="page">
      <h1>Downloads</h1>

      <p className="hint">
        Copies of works fetched from AO3 and kept here. Ask for one from a work’s own page — every
        request waits its turn behind the same rate limit every AO3 request shares, so a file appears a
        little after it is asked for rather than at once.
      </p>

      {error !== null && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      {downloads === null ? (
        !error && <SkeletonRows rows={3} kind="line" />
      ) : downloads.length === 0 ? (
        <EmptyState
          title="Nothing asked for yet"
          action={
            <Link className="button" to="/works">
              Open Works
            </Link>
          }
        >
          Open a work and pick a format to have it fetched.
        </EmptyState>
      ) : (
        <table className="downloads-table">
          <thead>
            <tr>
              <th>Work</th>
              <th>Format</th>
              <th>Status</th>
              <th className="numeric">Size</th>
              <th>Asked for</th>
              <th>Ready</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {downloads.map((download) => (
              <tr key={download.id}>
                <td className="download-work">
                  <Link className="title-link" to={`/works/${download.workId}`}>
                    {download.workTitle}
                  </Link>
                </td>
                <td>{download.format.toUpperCase()}</td>
                <td>
                  <span className="download-status" data-status={download.status}>
                    {DOWNLOAD_STATUS_LABELS[download.status]}
                  </span>
                  {/* The message names which half of the fetch failed — the work's page, or the
                      file itself — which is the difference between "AO3 took the work down" and
                      "try again later". Showing it beats a generic failure. */}
                  {download.errorMessage !== null && (
                    <span className="download-error">{download.errorMessage}</span>
                  )}
                </td>
                <td className="numeric">
                  {download.previousSizeBytes === null ? (
                    formatSize(download.sizeBytes)
                  ) : (
                    // A request that is out fetching a newer version reports no size of its own,
                    // which read as an em dash beside "Failed" as though the reader had nothing.
                    // What they have is the copy from before they asked.
                    <>
                      {formatSize(download.previousSizeBytes)}{' '}
                      <span className="hint">earlier copy</span>
                    </>
                  )}
                </td>
                <td>{formatDate(download.requestedAt)}</td>
                <td>{formatDate(download.completedAt)}</td>
                <td className="download-actions">
                  {download.status === 'Complete' && (
                    // A plain link, so the browser's own download machinery streams it to disk.
                    // `download` asks it to save rather than navigate; the server's
                    // Content-Disposition is what actually names the file, since this attribute is
                    // ignored on a cross-origin response and the header is not.
                    <a href={api.downloadFileUrl(download.id)} download>
                      Save
                    </a>
                  )}
                  {download.status === 'Complete' && download.format === 'Epub' && (
                    <Link to={`/works/${download.workId}/read`}>Read</Link>
                  )}
                  {/* The copy this reader had before they asked for a newer version. Offered
                      under a name that says which it is: the request itself has not finished, and
                      the point of keeping the reference is that a fetch that fails — or has simply
                      not happened yet — must not be what takes their file away. */}
                  {download.status !== 'Complete' && download.previousSizeBytes !== null && (
                    <a href={api.downloadFileUrl(download.id)} download>
                      Save earlier copy
                    </a>
                  )}
                  {download.status !== 'Complete' &&
                    download.previousSizeBytes !== null &&
                    download.format === 'Epub' && (
                      <Link to={`/works/${download.workId}/read`}>Read earlier copy</Link>
                    )}
                  {download.status === 'Failed' && (
                    <button
                      type="button"
                      className="link"
                      onClick={() => void request(download.workId, download.format)}
                    >
                      Try again
                    </button>
                  )}
                  <button type="button" className="link" onClick={() => void remove(download.id)}>
                    Remove
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <p className="hint">
        Removing a request drops it from this list. The file itself stays — another reader may hold
        a request for the same copy, and re-fetching it would cost AO3 a page load it has already
        served.
      </p>
    </div>
  );
}
