import { describe, it, expect, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import ThemeToggle from './ThemeToggle.jsx';

describe('<ThemeToggle />', () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
  });

  it('toggles the theme and persists the choice', async () => {
    render(<ThemeToggle />);

    // starts on light (matchMedia stub reports not-dark) -> offers "Switch to dark mode"
    await userEvent.click(screen.getByRole('button', { name: /switch to dark mode/i }));
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem('todo.theme')).toBe('dark');

    // now offers "Switch to light mode"
    await userEvent.click(screen.getByRole('button', { name: /switch to light mode/i }));
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    expect(localStorage.getItem('todo.theme')).toBe('light');
  });

  it('starts dark when that was the stored choice', () => {
    localStorage.setItem('todo.theme', 'dark');

    render(<ThemeToggle />);

    expect(screen.getByRole('button', { name: /switch to light mode/i })).toBeInTheDocument();
  });

  it('starts light when that was the stored choice, whatever the OS says', () => {
    localStorage.setItem('todo.theme', 'light');
    window.matchMedia = () => ({ matches: true, addEventListener() {}, removeEventListener() {} });

    render(<ThemeToggle />);

    expect(screen.getByRole('button', { name: /switch to dark mode/i })).toBeInTheDocument();
  });

  it('follows the OS preference until a choice is made', () => {
    window.matchMedia = () => ({ matches: true, addEventListener() {}, removeEventListener() {} });

    render(<ThemeToggle />);

    expect(screen.getByRole('button', { name: /switch to light mode/i })).toBeInTheDocument();
  });

  it('treats an unavailable matchMedia as light rather than failing', () => {
    window.matchMedia = () => { throw new Error('unsupported'); };

    render(<ThemeToggle />);

    expect(screen.getByRole('button', { name: /switch to dark mode/i })).toBeInTheDocument();
  });

  it('toggles back to light', async () => {
    localStorage.setItem('todo.theme', 'dark');
    render(<ThemeToggle />);

    await userEvent.click(screen.getByRole('button', { name: /switch to light mode/i }));

    expect(localStorage.getItem('todo.theme')).toBe('light');
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
  });
});
