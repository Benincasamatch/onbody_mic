import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { PreviewProvider } from './preview';
import './styles.css';

const root = document.getElementById('root');
if (!root) throw new Error('Missing preview root');
createRoot(root).render(<StrictMode><PreviewProvider><App /></PreviewProvider></StrictMode>);
