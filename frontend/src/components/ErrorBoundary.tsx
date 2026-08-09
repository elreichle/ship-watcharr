import { Component, type ErrorInfo, type ReactNode } from 'react';

interface Props {
  children: ReactNode;
  /**
   * What the caller is guarding. Used in the heading so the two boundaries read differently: a
   * page that failed leaves the sidebar usable, a shell that failed does not.
   */
  scope: 'page' | 'app';
}

interface State {
  error: Error | null;
}

/**
 * Stops one broken component from blanking the whole interface.
 *
 * React unmounts the entire tree when a render throws and nothing catches it, so before this
 * existed a single bad field in one API response left the app as an empty <body> — no message, no
 * sidebar, nothing to act on. That is indistinguishable from "the server is down" or "the build is
 * broken", which is a bad place to leave someone self-hosting this.
 *
 * Has to be a class: getDerivedStateFromError/componentDidCatch have no hook equivalent.
 */
export class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // The boundary swallows the error as far as React is concerned, so without this the stack —
    // the only thing that says *where* it broke — never reaches the console.
    console.error('Unhandled error in', this.props.scope, error, info.componentStack);
  }

  private retry = () => this.setState({ error: null });

  render(): ReactNode {
    const { error } = this.state;
    if (!error) return this.props.children;

    const isPage = this.props.scope === 'page';

    return (
      <div className="error-boundary" role="alert">
        <h1>{isPage ? 'This page hit an error' : 'Ship Watcharr hit an error'}</h1>
        <p>
          {isPage
            ? 'The rest of the app still works — the navigation on the left will take you elsewhere.'
            : 'Something failed outside of any single page, so there is nothing left to navigate with.'}
        </p>

        {/* The message is the whole point of showing this rather than a blank screen: on a
            self-hosted instance the person reading it is also the person who can fix it. */}
        <pre className="error-boundary-message">{error.message}</pre>

        <div className="error-boundary-actions">
          <button type="button" onClick={this.retry}>
            Try again
          </button>
          <button type="button" onClick={() => window.location.reload()}>
            Reload the page
          </button>
        </div>

        <p className="hint">
          If it keeps happening after a reload, the browser console has the full stack trace.
        </p>
      </div>
    );
  }
}
