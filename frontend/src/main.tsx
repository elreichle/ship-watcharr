import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
// Import order matters: obsidian-defaults.css opens with the `@layer obsidian-defaults, app;`
// statement that fixes layer precedence, and it has to be seen before any `@layer app { … }` block.
import './theme/obsidian-defaults.css'
import './theme/presets.css'
import './index.css'
import './styles/shell.css'
import { ThemeProvider } from './theme/ThemeContext'
import { ErrorBoundary } from './components/ErrorBoundary'
import App from './App.tsx'

// Outermost boundary: catches anything the per-page one can't see — the providers, the router
// itself, and the login/register pages that render outside the app shell. Last line of defence
// against a blank screen, so it deliberately sits above everything except the theme.
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ThemeProvider>
      <ErrorBoundary scope="app">
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </ErrorBoundary>
    </ThemeProvider>
  </StrictMode>,
)
