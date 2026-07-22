import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import ColorPicker from './ColorPicker.jsx';

describe('ColorPicker', () => {
  it('starts collapsed and opens the popover on click', async () => {
    render(<ColorPicker value="#4f46e5" onChange={() => {}} label="Category color" />);
    const trigger = screen.getByRole('button', { name: /category color: #4f46e5/i });
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    await userEvent.click(trigger);

    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('emits an API-valid #rrggbb color when the hex field changes', async () => {
    const onChange = vi.fn();
    render(<ColorPicker value="#4f46e5" onChange={onChange} label="Category color" />);
    await userEvent.click(screen.getByRole('button', { name: /category color/i }));

    const hex = screen.getByLabelText(/category color hex value/i);
    await userEvent.clear(hex);
    await userEvent.type(hex, '00ff00');

    expect(onChange).toHaveBeenLastCalledWith('#00ff00');
  });

  it('closes on Escape', async () => {
    render(<ColorPicker value="#4f46e5" onChange={() => {}} label="Category color" />);
    await userEvent.click(screen.getByRole('button', { name: /category color/i }));
    expect(screen.getByRole('dialog')).toBeInTheDocument();

    await userEvent.keyboard('{Escape}');

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});
