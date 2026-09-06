import type { ReactNode } from 'react';
import { APP_NAME } from '../appInfo';
import { Icon } from './Icon';

interface AuthFrameProps {
  /** The page's own title — "Log in" or "Register" — set inside the card. */
  title: string;
  children: ReactNode;
  /** The line under the card that leads to the other page. */
  footer: ReactNode;
}

/**
 * The frame both signed-out pages share: the wordmark, a card holding the form, and the way across
 * to the other page. Exists so the first screen a reader ever sees names the product, and so Log in
 * and Register cannot drift apart in shape.
 */
export function AuthFrame({ title, children, footer }: AuthFrameProps) {
  return (
    <div className="auth-page">
      <div className="auth-brand">
        <Icon name="bookmark" size="l" />
        <span>{APP_NAME}</span>
      </div>
      <div className="auth-card">
        <h1>{title}</h1>
        {children}
      </div>
      <p className="auth-switch">{footer}</p>
    </div>
  );
}
