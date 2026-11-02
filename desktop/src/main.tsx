import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { DesktopProvider } from './context';
import './styles.css';

const root = document.getElementById('root');
if (!root) throw new Error('Missing native desktop root');
createRoot(root).render(<StrictMode><DesktopProvider><App /></DesktopProvider></StrictMode>);
