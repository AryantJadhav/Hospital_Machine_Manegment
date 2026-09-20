import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App.tsx'
import { applyTheme, storedTheme } from './theme'

// Before the first paint, so a person who chose dark does not see light flash up.
applyTheme(storedTheme())

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
