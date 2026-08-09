import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
// Import order matters: obsidian-defaults.css opens with the `@layer obsidian-defaults, app;`
// statement that fixes layer precedence, and it has to be seen before any `@layer app { … }` block.
import './theme/obsidian-defaults.css'
import './index.css'
import './styles/shell.css'
import { ThemeProvider } from './theme/ThemeContext'
import App from './App.tsx'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ThemeProvider>
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </ThemeProvider>
  </StrictMode>,
)
