import { useEffect, useState } from 'react';
import { api } from '../api/client';
import type { ScrapedItem } from '../api/types';

export function ScrapedDataPage() {
  const [items, setItems] = useState<ScrapedItem[]>([]);

  useEffect(() => {
    api.getScrapedItems().then(setItems).catch(() => setItems([]));
  }, []);

  return (
    <div className="page">
      <h1>Scraped data</h1>
      <table className="jobs-table">
        <thead>
          <tr>
            <th>Scraped at</th>
            <th>Title</th>
            <th>Source URL</th>
            <th>Payload</th>
          </tr>
        </thead>
        <tbody>
          {items.map((item) => (
            <tr key={item.id}>
              <td>{new Date(item.scrapedAt).toLocaleString()}</td>
              <td>{item.title ?? '—'}</td>
              <td>
                <a href={item.sourceUrl} target="_blank" rel="noreferrer">
                  {item.sourceUrl}
                </a>
              </td>
              <td>
                <code>{item.payloadJson}</code>
              </td>
            </tr>
          ))}
          {items.length === 0 && (
            <tr>
              <td colSpan={4}>No scraped data yet.</td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  );
}
