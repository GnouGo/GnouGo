import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import './index.scss'

const rootElement = document.getElementById('root');

if (!rootElement) {
  throw new Error('Failed to find the root element');
}

createRoot(rootElement).render(
  <StrictMode>
    <App />
  </StrictMode>
)

