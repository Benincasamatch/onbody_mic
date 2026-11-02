import { useId, useLayoutEffect, useRef, useState } from 'react';
import type { KeyboardEvent } from 'react';
import { Icon } from './icons';

export interface SelectOption { value: string; label: string; disabled?: boolean }
interface SelectProps {
  value: string;
  options: SelectOption[];
  onChange(value: string): void;
  'aria-label': string;
  disabled?: boolean;
  required?: boolean;
  className?: string;
}

export function Select({ value, options, onChange, disabled = false, required = false, className = '', 'aria-label': label }: SelectProps) {
  const id = useId();
  const root = useRef<HTMLSpanElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const popup = useRef<HTMLDivElement>(null);
  const typeahead = useRef({ text: '', time: 0 });
  const [expanded, setExpanded] = useState(false);
  const [active, setActive] = useState(-1);
  const [invalid, setInvalid] = useState(false);
  const selected = options.findIndex(option => option.value === value);
  const enabled = options.map((option, index) => option.disabled ? -1 : index).filter(index => index >= 0);

  useLayoutEffect(() => {
    if (!expanded || disabled) return;
    const element = popup.current;
    const button = trigger.current;
    if (!element || !button) return;
    function position() {
      if (!element || !button) return;
      const rect = button.getBoundingClientRect();
      const width = Math.min(Math.max(rect.width, 180), window.innerWidth - 16);
      const below = window.innerHeight - rect.bottom - 12;
      const above = rect.top - 12;
      const upwards = below < 180 && above > below;
      const height = Math.max(40, Math.min(280, upwards ? above : below));
      element.style.width = `${width}px`;
      element.style.maxHeight = `${height}px`;
      element.style.left = `${Math.max(8, Math.min(rect.left, window.innerWidth - width - 8))}px`;
      element.style.top = upwards ? 'auto' : `${rect.bottom + 5}px`;
      element.style.bottom = upwards ? `${window.innerHeight - rect.top + 5}px` : 'auto';
    }
    position();
    element.showPopover();
    function outside(event: PointerEvent) {
      if (!root.current?.contains(event.target as Node)) setExpanded(false);
    }
    document.addEventListener('pointerdown', outside, true);
    window.addEventListener('resize', position);
    window.addEventListener('scroll', position, true);
    return () => {
      element.hidePopover();
      document.removeEventListener('pointerdown', outside, true);
      window.removeEventListener('resize', position);
      window.removeEventListener('scroll', position, true);
    };
  }, [expanded, disabled]);

  useLayoutEffect(() => {
    if (expanded && active >= 0) popup.current?.querySelector<HTMLElement>(`[data-option-index="${active}"]`)?.scrollIntoView({ block: 'nearest' });
  }, [expanded, active]);

  function open(last = false) {
    if (disabled) return;
    setActive(selected >= 0 && !options[selected].disabled ? selected : (last ? enabled.at(-1) : enabled[0]) ?? -1);
    setExpanded(true);
  }
  function choose(index: number) {
    const option = options[index];
    if (!option || option.disabled) return;
    onChange(option.value);
    setInvalid(false);
    setExpanded(false);
    trigger.current?.focus();
  }
  function keyboard(event: KeyboardEvent<HTMLButtonElement>) {
    if (disabled) return;
    if (event.key === 'Escape' && expanded) {
      event.preventDefault(); event.stopPropagation(); setExpanded(false); return;
    }
    if (event.key === 'Tab') { setExpanded(false); return; }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      if (!expanded) { open(event.key === 'ArrowUp'); return; }
      const index = enabled.indexOf(active);
      setActive(enabled[(index + (event.key === 'ArrowDown' ? 1 : -1) + enabled.length) % enabled.length] ?? -1);
      return;
    }
    if (event.key === 'Home' || event.key === 'End') {
      event.preventDefault();
      const index = event.key === 'Home' ? enabled[0] : enabled.at(-1);
      if (expanded) setActive(index ?? -1); else choose(index ?? -1);
      return;
    }
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      if (expanded) choose(active); else open();
      return;
    }
    if (event.key.length === 1 && !event.ctrlKey && !event.altKey && !event.metaKey) {
      event.preventDefault();
      const now = Date.now();
      const previous = now - typeahead.current.time < 700 ? typeahead.current.text : '';
      const char = event.key.toLocaleLowerCase();
      const text = previous === char ? char : previous + char;
      typeahead.current = { text, time: now };
      const current = expanded ? active : selected;
      const start = enabled.indexOf(current);
      const ordered = [...enabled.slice(start + 1), ...enabled.slice(0, start + 1)];
      const index = ordered.find(index => options[index].label.toLocaleLowerCase().startsWith(text));
      if (index !== undefined) { if (expanded) setActive(index); else choose(index); }
    }
  }

  return <span ref={root} className={`styled-select ${className}`} onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget)) setExpanded(false); }}>
    <button ref={trigger} type="button" role="combobox" aria-label={label} aria-haspopup="listbox" aria-expanded={expanded && !disabled} aria-controls={`${id}-list`} aria-activedescendant={expanded && active >= 0 ? `${id}-${active}` : undefined} aria-required={required} aria-invalid={invalid || undefined} disabled={disabled} className="select-trigger" onClick={() => { if (expanded) setExpanded(false); else open(); }} onKeyDown={keyboard}><span>{options[selected]?.label ?? '请选择'}</span><Icon name="chevron" size={12} /></button>
    {required && <input className="select-validation" aria-hidden="true" tabIndex={-1} value={value} onChange={() => undefined} required disabled={disabled} onInvalid={event => { event.preventDefault(); setInvalid(true); trigger.current?.focus(); }} />}
    <div ref={popup} id={`${id}-list`} popover="manual" role="listbox" aria-label={label} className="select-popup">{options.map((option, index) => <div key={option.value} id={`${id}-${index}`} role="option" aria-selected={option.value === value} aria-disabled={option.disabled || undefined} data-option-index={index} className={`select-option ${index === active ? 'highlighted' : ''}`} onPointerMove={() => { if (!option.disabled) setActive(index); }} onPointerDown={event => event.preventDefault()} onClick={() => choose(index)}><span>{option.label}</span>{option.value === value && <Icon name="check" size={13} />}</div>)}</div>
  </span>;
}
