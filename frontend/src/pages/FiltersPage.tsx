import { useCallback, useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { EmptyState } from '../components/EmptyState';
import { SkeletonRows } from '../components/Skeleton';
import type {
  Ao3TagType,
  FilterVocabulary,
  ReadingStatus,
  SavedFilter,
  SavedFilterAuthor,
  SavedFilterTag,
  SaveFilterInput,
  VocabularyOption,
  WatchedShip,
  WorkSort,
} from '../api/types';
import {
  describeHalfStars,
  HALF_STAR_VALUES,
  READING_STATUS_LABELS,
  READING_STATUSES,
} from '../readingStatus';

/**
 * How each status reads as a criterion rather than as a mark. Only "None" differs: on a Works row
 * it means "you have not said", while as a filter it is the thing people actually want to ask for.
 */
const READING_STATUS_CRITERION_LABELS: Record<ReadingStatus, string> = {
  ...READING_STATUS_LABELS,
  None: 'Unread — not marked at all',
};

const TAG_TYPES: Ao3TagType[] = ['Freeform', 'Relationship', 'Character', 'Fandom', 'Warning'];

/**
 * A filter being edited. Tags and authors are held as whole objects rather than as the id lists the
 * API takes, because the editor has to render their names — and a chip that has to look up its own
 * label is a request per chip.
 */
interface Draft
  extends Omit<
    SaveFilterInput,
    'includeTagIds' | 'excludeTagIds' | 'includeAuthorIds' | 'excludeAuthorIds'
  > {
  includeTags: SavedFilterTag[];
  excludeTags: SavedFilterTag[];
  includeAuthors: SavedFilterAuthor[];
  excludeAuthors: SavedFilterAuthor[];
}

function emptyDraft(): Draft {
  return {
    name: '',
    isDefault: false,
    shipId: null,
    isComplete: null,
    minWordCount: null,
    maxWordCount: null,
    minChapterCount: null,
    maxChapterCount: null,
    minKudos: null,
    maxKudos: null,
    minHits: null,
    maxHits: null,
    minComments: null,
    maxComments: null,
    minBookmarks: null,
    maxBookmarks: null,
    minRating: null,
    maxRating: null,
    readingStatus: null,
    minUserRating: null,
    maxUserRating: null,
    includeCategories: [],
    excludeCategories: [],
    includeWarnings: [],
    excludeWarnings: [],
    languageCode: null,
    updatedAfter: null,
    updatedBefore: null,
    sort: 'updated',
    ascending: false,
    includeTags: [],
    excludeTags: [],
    includeAuthors: [],
    excludeAuthors: [],
  };
}

/**
 * Timestamps are cut back to the date part, because that is what a date input can hold. The
 * reverse trip re-widens them — see `toRequest`.
 */
function draftFrom(filter: SavedFilter): Draft {
  // Named only to be dropped: the server owns these, and carrying them into the draft would have
  // the editor post back an id, a creation time and a match count it has no business setting.
  const {
    id: _id,
    shipTagName: _shipTagName,
    matchingWorkCount: _matchingWorkCount,
    createdAt: _createdAt,
    updatedAt: _updatedAt,
    ...criteria
  } = filter;

  return {
    ...criteria,
    updatedAfter: filter.updatedAfter?.slice(0, 10) ?? null,
    updatedBefore: filter.updatedBefore?.slice(0, 10) ?? null,
  };
}

function toRequest(draft: Draft): SaveFilterInput {
  const { includeTags, excludeTags, includeAuthors, excludeAuthors, ...criteria } = draft;

  return {
    ...criteria,
    name: draft.name.trim(),

    // Both bounds are inclusive on the server, and a date input yields only a day. Sending the bare
    // date for the upper bound would compare against midnight and so exclude the whole day the user
    // picked; widening it here is what makes "up to the 5th" mean the 5th.
    updatedAfter: draft.updatedAfter ? `${draft.updatedAfter}T00:00:00.000Z` : null,
    updatedBefore: draft.updatedBefore ? `${draft.updatedBefore}T23:59:59.999Z` : null,

    includeTagIds: includeTags.map((tag) => tag.tagId),
    excludeTagIds: excludeTags.map((tag) => tag.tagId),
    includeAuthorIds: includeAuthors.map((author) => author.pseudId),
    excludeAuthorIds: excludeAuthors.map((author) => author.pseudId),
  };
}

/** '' is how an emptied number input reads, and it means "no bound" rather than zero. */
function numberOrNull(value: string): number | null {
  if (value.trim() === '') return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function labelFor(options: VocabularyOption[], value: string): string {
  return options.find((option) => option.value === value)?.label ?? value;
}

/**
 * A one-line account of what a set narrows by, for the list. Deliberately lists every criterion
 * rather than the first few: the reason to read this line is to check a set does what its name
 * claims, and a truncated summary is exactly where a forgotten criterion would hide.
 */
function describeFilter(filter: SavedFilter, vocabulary: FilterVocabulary | null): string {
  const parts: string[] = [];
  const labels = (options: VocabularyOption[] | undefined, values: string[]) =>
    values.map((value) => (options ? labelFor(options, value) : value)).join(', ');

  const range = (what: string, min: number | null, max: number | null) => {
    if (min !== null && max !== null) parts.push(`${what} ${min.toLocaleString()}–${max.toLocaleString()}`);
    else if (min !== null) parts.push(`${what} from ${min.toLocaleString()}`);
    else if (max !== null) parts.push(`${what} up to ${max.toLocaleString()}`);
  };

  if (filter.shipTagName) parts.push(filter.shipTagName);
  if (filter.isComplete === true) parts.push('complete');
  if (filter.isComplete === false) parts.push('work in progress');

  if (filter.minRating || filter.maxRating) {
    const from = filter.minRating ? labels(vocabulary?.ratings, [filter.minRating]) : 'any';
    const to = filter.maxRating ? labels(vocabulary?.ratings, [filter.maxRating]) : 'any';
    parts.push(`rated ${from} to ${to}`);
  }

  range('words', filter.minWordCount, filter.maxWordCount);
  range('chapters', filter.minChapterCount, filter.maxChapterCount);
  range('kudos', filter.minKudos, filter.maxKudos);
  range('hits', filter.minHits, filter.maxHits);
  range('comments', filter.minComments, filter.maxComments);
  range('bookmarks', filter.minBookmarks, filter.maxBookmarks);

  if (filter.includeCategories.length > 0)
    parts.push(`any of ${labels(vocabulary?.categories, filter.includeCategories)}`);
  if (filter.excludeCategories.length > 0)
    parts.push(`not ${labels(vocabulary?.categories, filter.excludeCategories)}`);
  if (filter.includeWarnings.length > 0)
    parts.push(`warns ${labels(vocabulary?.warnings, filter.includeWarnings)}`);
  if (filter.excludeWarnings.length > 0)
    parts.push(`no ${labels(vocabulary?.warnings, filter.excludeWarnings)}`);

  if (filter.readingStatus)
    parts.push(
      filter.readingStatus === 'None' ? 'unread' : `marked ${READING_STATUS_LABELS[filter.readingStatus].toLowerCase()}`,
    );

  if (filter.minUserRating !== null && filter.maxUserRating !== null)
    parts.push(`you rated ${describeHalfStars(filter.minUserRating)}–${describeHalfStars(filter.maxUserRating)}`);
  else if (filter.minUserRating !== null)
    parts.push(`you rated ${describeHalfStars(filter.minUserRating)} or more`);
  else if (filter.maxUserRating !== null)
    parts.push(`you rated ${describeHalfStars(filter.maxUserRating)} or less`);

  if (filter.languageCode)
    parts.push(`in ${vocabulary ? labelFor(vocabulary.languages, filter.languageCode) : filter.languageCode}`);

  if (filter.updatedAfter) parts.push(`updated after ${new Date(filter.updatedAfter).toLocaleDateString()}`);
  if (filter.updatedBefore) parts.push(`updated before ${new Date(filter.updatedBefore).toLocaleDateString()}`);

  if (filter.includeTags.length > 0)
    parts.push(`tagged ${filter.includeTags.map((tag) => tag.name).join(' + ')}`);
  if (filter.excludeTags.length > 0)
    parts.push(`not tagged ${filter.excludeTags.map((tag) => tag.name).join(', ')}`);
  if (filter.includeAuthors.length > 0)
    parts.push(`by ${filter.includeAuthors.map((author) => author.displayName).join(' or ')}`);
  if (filter.excludeAuthors.length > 0)
    parts.push(`not by ${filter.excludeAuthors.map((author) => author.displayName).join(', ')}`);

  return parts.length > 0 ? parts.join(' · ') : 'Everything you follow';
}

export function FiltersPage() {
  const [filters, setFilters] = useState<SavedFilter[] | null>(null);
  const [vocabulary, setVocabulary] = useState<FilterVocabulary | null>(null);
  const [ships, setShips] = useState<WatchedShip[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<number | null>(null);

  /** The set being edited, or a blank draft for a new one. Null when the editor is closed. */
  const [editing, setEditing] = useState<{ id: number | null; draft: Draft } | null>(null);

  const load = () => api.getSavedFilters().then(setFilters);

  useEffect(() => {
    load().catch((err) =>
      setError(err instanceof ApiError ? err.message : 'Failed to load your saved filters.'),
    );

    // The vocabulary and the ship list are what the editor is built from, but neither is the page.
    // A failure in either leaves the list below readable, and the editor says what it is missing.
    api.getFilterVocabulary().then(setVocabulary).catch(() => setVocabulary(null));
    api.getWatchedShips().then((response) => setShips(response.ships)).catch(() => setShips([]));
  }, []);

  const toggleDefault = async (filter: SavedFilter) => {
    setError(null);
    setBusyId(filter.id);
    try {
      await api.setSavedFilterDefault(filter.id, !filter.isDefault);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to change the default filter.');
    } finally {
      setBusyId(null);
    }
  };

  const remove = async (filter: SavedFilter) => {
    setError(null);
    setBusyId(filter.id);
    try {
      await api.deleteSavedFilter(filter.id);
      if (editing?.id === filter.id) setEditing(null);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to delete that filter.');
    } finally {
      setBusyId(null);
    }
  };

  return (
    <div className="page">
      <h1>Filters</h1>

      <p className="hint">
        A filter is a named set of criteria for narrowing your library — AO3’s filter sidebar, saved
        and reusable. Pick one on the <Link to="/works">Works</Link> tab, or mark one as your default
        and it applies whenever you open Works without choosing.
      </p>

      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      {editing === null ? (
        // With no filters yet the empty state below offers the same button, so it is not repeated.
        filters !== null &&
        filters.length > 0 && (
          <div className="button-row">
            <button type="button" onClick={() => setEditing({ id: null, draft: emptyDraft() })}>
              New filter
            </button>
          </div>
        )
      ) : (
        <FilterEditor
          key={editing.id ?? 'new'}
          filterId={editing.id}
          draft={editing.draft}
          vocabulary={vocabulary}
          ships={ships}
          onCancel={() => setEditing(null)}
          onSaved={async () => {
            setEditing(null);
            await load().catch(() => setError('Saved, but the list failed to refresh.'));
          }}
        />
      )}

      {filters === null ? (
        !error && <SkeletonRows rows={3} kind="line" />
      ) : filters.length === 0 ? (
        editing === null && (
          <EmptyState
            title="No filters yet"
            action={
              <button type="button" onClick={() => setEditing({ id: null, draft: emptyDraft() })}>
                New filter
              </button>
            }
          >
            One is worth making as soon as “the fics I actually want to read” stops being everything
            you follow.
          </EmptyState>
        )
      ) : (
        <table className="filters-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Narrows by</th>
              <th className="numeric">Matches</th>
              <th>Opens by default</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {filters.map((filter) => (
              <tr key={filter.id}>
                <td className="filter-name">
                  {filter.name}
                  <span className="filter-sort">
                    sorted by {vocabulary ? labelFor(vocabulary.sorts, filter.sort) : filter.sort},{' '}
                    {filter.ascending ? 'lowest first' : 'highest first'}
                  </span>
                </td>
                <td className="filter-summary">{describeFilter(filter, vocabulary)}</td>
                <td className="numeric">
                  <Link to={`/works?filter=${filter.id}`}>
                    {filter.matchingWorkCount.toLocaleString()}
                  </Link>
                </td>
                <td>
                  <label className="filter-default">
                    <input
                      type="checkbox"
                      checked={filter.isDefault}
                      disabled={busyId === filter.id}
                      onChange={() => void toggleDefault(filter)}
                    />
                    {filter.isDefault ? 'Yes' : 'No'}
                  </label>
                </td>
                <td className="filter-actions">
                  <button
                    type="button"
                    className="link"
                    onClick={() => setEditing({ id: filter.id, draft: draftFrom(filter) })}
                  >
                    Edit
                  </button>
                  <button
                    type="button"
                    className="link"
                    disabled={busyId === filter.id}
                    onClick={() => void remove(filter)}
                  >
                    {busyId === filter.id ? 'Working…' : 'Delete'}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {filters !== null && filters.length > 0 && (
        <p className="hint">
          “Matches” counts the works in your library this filter currently selects — follow it to see
          them. A filter naming a ship you’ve since unfollowed stays saved and simply matches nothing.
        </p>
      )}
    </div>
  );
}

interface FilterEditorProps {
  /** Null for a new set. */
  filterId: number | null;
  draft: Draft;
  vocabulary: FilterVocabulary | null;
  ships: WatchedShip[];
  onCancel: () => void;
  onSaved: () => void | Promise<void>;
}

function FilterEditor({ filterId, draft, vocabulary, ships, onCancel, onSaved }: FilterEditorProps) {
  const [value, setValue] = useState<Draft>(draft);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const set = <K extends keyof Draft>(key: K, next: Draft[K]) =>
    setValue((current) => ({ ...current, [key]: next }));

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();

    setError(null);
    setSaving(true);
    try {
      const request = toRequest(value);
      if (filterId === null) await api.createSavedFilter(request);
      else await api.updateSavedFilter(filterId, request);
      await onSaved();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to save that filter.');
    } finally {
      setSaving(false);
    }
  };

  return (
    <form className="filter-editor" onSubmit={(e) => void onSubmit(e)}>
      <h2>{filterId === null ? 'New filter' : `Editing ${draft.name}`}</h2>

      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      <Section title="Name">
        <label className="filter-field filter-field-wide">
          What to call it
          <input
            name="name"
            autoComplete="off"
            value={value.name}
            onChange={(e) => set('name', e.target.value)}
            placeholder="Long finished fics"
            maxLength={100}
          />
        </label>

        <label className="filter-check">
          <input
            type="checkbox"
            checked={value.isDefault}
            onChange={(e) => set('isDefault', e.target.checked)}
          />
          Open Works with this filter by default
        </label>
      </Section>

      <Section title="Ship and status">
        <label className="filter-field">
          Ship
          <select
            value={value.shipId ?? ''}
            onChange={(e) => set('shipId', e.target.value === '' ? null : Number(e.target.value))}
          >
            <option value="">Any ship you follow</option>
            {ships.map((ship) => (
              <option key={ship.shipId} value={ship.shipId}>
                {ship.tagName}
              </option>
            ))}
          </select>
        </label>

        <label className="filter-field">
          Completion
          {/* Three states, not a checkbox: "either" is a real answer and the commonest one. */}
          <select
            value={value.isComplete === null ? '' : String(value.isComplete)}
            onChange={(e) => set('isComplete', e.target.value === '' ? null : e.target.value === 'true')}
          >
            <option value="">Complete or not</option>
            <option value="true">Complete only</option>
            <option value="false">Works in progress only</option>
          </select>
        </label>

        <label className="filter-field">
          Language
          <select
            value={value.languageCode ?? ''}
            onChange={(e) => set('languageCode', e.target.value === '' ? null : e.target.value)}
          >
            <option value="">Any language</option>
            {vocabulary?.languages.map((language) => (
              <option key={language.value} value={language.value}>
                {language.label}
              </option>
            ))}
          </select>
        </label>
      </Section>

      <Section title="Rating">
        <label className="filter-field">
          At least
          <select
            value={value.minRating ?? ''}
            onChange={(e) => set('minRating', e.target.value === '' ? null : e.target.value)}
          >
            <option value="">Any</option>
            {vocabulary?.ratings.map((rating) => (
              <option key={rating.value} value={rating.value}>
                {rating.label}
              </option>
            ))}
          </select>
        </label>

        <label className="filter-field">
          At most
          <select
            value={value.maxRating ?? ''}
            onChange={(e) => set('maxRating', e.target.value === '' ? null : e.target.value)}
          >
            <option value="">Any</option>
            {vocabulary?.ratings.map((rating) => (
              <option key={rating.value} value={rating.value}>
                {rating.label}
              </option>
            ))}
          </select>
        </label>

        <p className="hint filter-note">
          Both ends are inclusive. Works whose rating AO3 never showed us sort below every real one,
          so an upper bound on its own keeps them — set a lower bound too if you’d rather not see them.
        </p>
      </Section>

      <Section title="Your own reading">
        <label className="filter-field">
          Reading status
          <select
            value={value.readingStatus ?? ''}
            onChange={(e) =>
              set('readingStatus', e.target.value === '' ? null : (e.target.value as ReadingStatus))
            }
          >
            <option value="">Any — read or not</option>
            {READING_STATUSES.map((status) => (
              <option key={status} value={status}>
                {READING_STATUS_CRITERION_LABELS[status]}
              </option>
            ))}
          </select>
        </label>

        <label className="filter-field">
          Your rating, at least
          <select
            value={value.minUserRating ?? ''}
            onChange={(e) => set('minUserRating', e.target.value === '' ? null : Number(e.target.value))}
          >
            <option value="">Any</option>
            {HALF_STAR_VALUES.map((half) => (
              <option key={half} value={half}>
                {describeHalfStars(half)}
              </option>
            ))}
          </select>
        </label>

        <label className="filter-field">
          Your rating, at most
          <select
            value={value.maxUserRating ?? ''}
            onChange={(e) => set('maxUserRating', e.target.value === '' ? null : Number(e.target.value))}
          >
            <option value="">Any</option>
            {HALF_STAR_VALUES.map((half) => (
              <option key={half} value={half}>
                {describeHalfStars(half)}
              </option>
            ))}
          </select>
        </label>

        <p className="hint filter-note">
          Your marks, not the archive’s — “Unread” covers everything you have never touched, not just
          works you cleared. A rating bound drops works you have not rated: unrated is not a score.
        </p>
      </Section>

      <Section title="Size and popularity">
        <Range
          label="Words"
          min={value.minWordCount}
          max={value.maxWordCount}
          onMin={(next) => set('minWordCount', next)}
          onMax={(next) => set('maxWordCount', next)}
        />
        <Range
          label="Chapters"
          min={value.minChapterCount}
          max={value.maxChapterCount}
          onMin={(next) => set('minChapterCount', next)}
          onMax={(next) => set('maxChapterCount', next)}
        />
        <Range
          label="Kudos"
          min={value.minKudos}
          max={value.maxKudos}
          onMin={(next) => set('minKudos', next)}
          onMax={(next) => set('maxKudos', next)}
        />
        <Range
          label="Hits"
          min={value.minHits}
          max={value.maxHits}
          onMin={(next) => set('minHits', next)}
          onMax={(next) => set('maxHits', next)}
        />
        <Range
          label="Comments"
          min={value.minComments}
          max={value.maxComments}
          onMin={(next) => set('minComments', next)}
          onMax={(next) => set('maxComments', next)}
        />
        <Range
          label="Bookmarks"
          min={value.minBookmarks}
          max={value.maxBookmarks}
          onMin={(next) => set('minBookmarks', next)}
          onMax={(next) => set('maxBookmarks', next)}
        />
      </Section>

      <Section title="Categories">
        <Flags
          legend="Must be at least one of"
          options={vocabulary?.categories ?? []}
          selected={value.includeCategories}
          onChange={(next) => set('includeCategories', next)}
        />
        <Flags
          legend="Must be none of"
          options={vocabulary?.categories ?? []}
          selected={value.excludeCategories}
          onChange={(next) => set('excludeCategories', next)}
        />
      </Section>

      <Section title="Archive warnings">
        <Flags
          legend="Must carry at least one of"
          options={vocabulary?.warnings ?? []}
          selected={value.includeWarnings}
          onChange={(next) => set('includeWarnings', next)}
        />
        <Flags
          legend="Must carry none of"
          options={vocabulary?.warnings ?? []}
          selected={value.excludeWarnings}
          onChange={(next) => set('excludeWarnings', next)}
        />
      </Section>

      <Section title="Last updated">
        <label className="filter-field">
          On or after
          <input
            type="date"
            value={value.updatedAfter ?? ''}
            onChange={(e) => set('updatedAfter', e.target.value === '' ? null : e.target.value)}
          />
        </label>
        <label className="filter-field">
          On or before
          <input
            type="date"
            value={value.updatedBefore ?? ''}
            onChange={(e) => set('updatedBefore', e.target.value === '' ? null : e.target.value)}
          />
        </label>
      </Section>

      <Section title="Tags">
        <TagPicker
          legend="Must have all of"
          selected={value.includeTags}
          onChange={(next) => set('includeTags', next)}
        />
        <TagPicker
          legend="Must have none of"
          selected={value.excludeTags}
          onChange={(next) => set('excludeTags', next)}
        />
        <p className="hint filter-note">
          Required tags narrow together, the way ticking two boxes on AO3 does: a work has to carry
          every one of them. Excluded tags each remove a work on their own.
        </p>
      </Section>

      <Section title="Authors">
        <AuthorPicker
          legend="Written by any of"
          selected={value.includeAuthors}
          onChange={(next) => set('includeAuthors', next)}
        />
        <AuthorPicker
          legend="Not written by"
          selected={value.excludeAuthors}
          onChange={(next) => set('excludeAuthors', next)}
        />
      </Section>

      <Section title="Order">
        <label className="filter-field">
          Sort by
          <select value={value.sort} onChange={(e) => set('sort', e.target.value as WorkSort)}>
            {(vocabulary?.sorts ?? [{ value: 'updated', label: 'Last updated' }]).map((sort) => (
              <option key={sort.value} value={sort.value}>
                {sort.label}
              </option>
            ))}
          </select>
        </label>

        <label className="filter-field">
          Order
          <select
            value={value.ascending ? 'true' : 'false'}
            onChange={(e) => set('ascending', e.target.value === 'true')}
          >
            <option value="false">Highest first</option>
            <option value="true">Lowest first</option>
          </select>
        </label>

        <p className="hint filter-note">
          The order this filter opens with. Changing the sort on the Works tab still overrides it.
        </p>
      </Section>

      <div className="button-row">
        <button type="submit" disabled={saving || value.name.trim() === ''}>
          {saving ? 'Saving…' : filterId === null ? 'Create filter' : 'Save changes'}
        </button>
        <button type="button" className="link" onClick={onCancel} disabled={saving}>
          Cancel
        </button>
      </div>
    </form>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <fieldset className="filter-section">
      <legend>{title}</legend>
      <div className="filter-section-body">{children}</div>
    </fieldset>
  );
}

interface RangeProps {
  label: string;
  min: number | null;
  max: number | null;
  onMin: (value: number | null) => void;
  onMax: (value: number | null) => void;
}

/** An inclusive pair of bounds. Either side may be left empty, which leaves it open. */
function Range({ label, min, max, onMin, onMax }: RangeProps) {
  return (
    <div className="filter-range">
      <span className="filter-range-label">{label}</span>
      <input
        type="number"
        min={0}
        value={min ?? ''}
        placeholder="no minimum"
        aria-label={`Minimum ${label.toLowerCase()}`}
        onChange={(e) => onMin(numberOrNull(e.target.value))}
      />
      <span aria-hidden="true">to</span>
      <input
        type="number"
        min={0}
        value={max ?? ''}
        placeholder="no maximum"
        aria-label={`Maximum ${label.toLowerCase()}`}
        onChange={(e) => onMax(numberOrNull(e.target.value))}
      />
    </div>
  );
}

interface FlagsProps {
  legend: string;
  options: VocabularyOption[];
  selected: string[];
  onChange: (values: string[]) => void;
}

function Flags({ legend, options, selected, onChange }: FlagsProps) {
  const toggle = (value: string, checked: boolean) =>
    onChange(checked ? [...selected, value] : selected.filter((each) => each !== value));

  return (
    <fieldset className="filter-flags">
      <legend>{legend}</legend>
      {options.length === 0 ? (
        <span className="hint">Couldn’t load these options.</span>
      ) : (
        options.map((option) => (
          <label key={option.value} className="filter-check">
            <input
              type="checkbox"
              checked={selected.includes(option.value)}
              onChange={(e) => toggle(option.value, e.target.checked)}
            />
            {option.label}
          </label>
        ))
      )}
    </fieldset>
  );
}

interface TagPickerProps {
  legend: string;
  selected: SavedFilterTag[];
  onChange: (tags: SavedFilterTag[]) => void;
}

function TagPicker({ legend, selected, onChange }: TagPickerProps) {
  const [type, setType] = useState<Ao3TagType>('Freeform');

  // Stable across renders, and only across them: the picker's search effect depends on this, so a
  // fresh closure every render would restart the search on its own result and never settle.
  const search = useCallback((q: string) => api.searchTags(q, type), [type]);

  return (
    <TokenPicker
      legend={legend}
      placeholder="Search your library’s tags…"
      selected={selected.map((tag) => ({ key: tag.tagId, label: tag.name, detail: tag.type }))}
      onRemove={(key) => onChange(selected.filter((tag) => tag.tagId !== key))}
      search={search}
      // Keyed by id, not by name: "Fluff" the freeform and "Fluff" the character are different
      // criteria, and AO3 has plenty of tags whose names collide across types.
      describe={(tag) => ({ key: tag.tagId, label: tag.name, detail: tag.type })}
      isSelected={(tag) => selected.some((each) => each.tagId === tag.tagId)}
      onPick={(tag) => onChange([...selected, tag])}
      before={
        <select value={type} onChange={(e) => setType(e.target.value as Ao3TagType)} aria-label="Tag type">
          {TAG_TYPES.map((each) => (
            <option key={each} value={each}>
              {each}
            </option>
          ))}
        </select>
      }
    />
  );
}

interface AuthorPickerProps {
  legend: string;
  selected: SavedFilterAuthor[];
  onChange: (authors: SavedFilterAuthor[]) => void;
}

function AuthorPicker({ legend, selected, onChange }: AuthorPickerProps) {
  // See the note in TagPicker: the search function has to be stable or the effect never settles.
  const search = useCallback((q: string) => api.searchAuthors(q), []);

  return (
    <TokenPicker
      legend={legend}
      placeholder="Search your library’s authors…"
      selected={selected.map((author) => ({
        key: author.pseudId,
        label: author.displayName,
        detail: author.username,
      }))}
      onRemove={(key) => onChange(selected.filter((author) => author.pseudId !== key))}
      search={search}
      describe={(author) => ({
        key: author.pseudId,
        label: author.displayName,
        detail: author.username,
      })}
      isSelected={(author) => selected.some((each) => each.pseudId === author.pseudId)}
      onPick={(author) => onChange([...selected, author])}
    />
  );
}

interface Token {
  key: number;
  label: string;
  detail?: string;
}

interface TokenPickerProps<T> {
  legend: string;
  placeholder: string;
  selected: Token[];
  onRemove: (key: number) => void;
  search: (q: string) => Promise<T[]>;
  describe: (item: T) => Token;
  isSelected: (item: T) => boolean;
  onPick: (item: T) => void;
  before?: ReactNode;
}

/**
 * Type-to-search over one of the lookup endpoints, with what you picked shown as removable chips.
 *
 * Suggestions come from the server rather than from a list held here, because the candidates are
 * every tag or author in the reader's library — which is far too many to ship to the page, and
 * grows with every scrape.
 */
function TokenPicker<T>({
  legend,
  placeholder,
  selected,
  onRemove,
  search,
  describe,
  isSelected,
  onPick,
  before,
}: TokenPickerProps<T>) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<T[]>([]);
  const [searching, setSearching] = useState(false);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    const trimmed = query.trim();
    if (trimmed === '') {
      setResults([]);
      setFailed(false);
      return;
    }

    // Debounced, and cancelled by the flag rather than by aborting: a keystroke mid-flight must not
    // let a stale response overwrite a newer one, which is what makes suggestions flicker backwards.
    let current = true;
    setSearching(true);

    const timer = setTimeout(() => {
      search(trimmed)
        .then((found) => {
          if (!current) return;
          setResults(found);
          setFailed(false);
        })
        .catch(() => {
          if (current) setFailed(true);
        })
        .finally(() => {
          if (current) setSearching(false);
        });
    }, 250);

    return () => {
      current = false;
      clearTimeout(timer);
    };
  }, [query, search]);

  return (
    <fieldset className="filter-tokens">
      <legend>{legend}</legend>

      <div className="filter-token-search">
        {before}
        <input
          type="search"
          autoComplete="off"
          value={query}
          placeholder={placeholder}
          onChange={(e) => setQuery(e.target.value)}
          aria-label={legend}
        />
      </div>

      {selected.length > 0 && (
        <div className="work-chips">
          {selected.map((token) => (
            <span key={token.key} className="chip">
              {token.label}
              <button
                type="button"
                className="chip-remove"
                aria-label={`Remove ${token.label}`}
                onClick={() => onRemove(token.key)}
              >
                ×
              </button>
            </span>
          ))}
        </div>
      )}

      {failed ? (
        <p className="hint">Couldn’t search just now.</p>
      ) : query.trim() !== '' && !searching && results.length === 0 ? (
        <p className="hint">Nothing in your library matches that.</p>
      ) : (
        results.length > 0 && (
          <ul className="filter-suggestions">
            {results.map((item) => {
              const token = describe(item);
              return (
                <li key={token.key}>
                  <button
                    type="button"
                    className="link"
                    disabled={isSelected(item)}
                    onClick={() => {
                      onPick(item);
                      setQuery('');
                    }}
                  >
                    {token.label}
                    {token.detail && <span className="filter-suggestion-detail">{token.detail}</span>}
                  </button>
                </li>
              );
            })}
          </ul>
        )
      )}
    </fieldset>
  );
}
