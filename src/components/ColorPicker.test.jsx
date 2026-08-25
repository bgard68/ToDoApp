import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import ColorPicker from './ColorPicker.jsx';

// jsdom has no PointerEvent, and fireEvent.pointerDown drops clientX/clientY on the fallback
// event — which silently feeds NaN into the wheel maths. A MouseEvent carries the coordinates
// and still triggers React's onPointerDown handlers.
function pointer(type, coords = {}) {
  return new MouseEvent(type, { bubbles: true, cancelable: true, ...coords });
}

/** jsdom lays nothing out, so the wheel's 156px box has to be supplied. */
function measureWheel(wheel) {
  vi.spyOn(wheel, 'getBoundingClientRect').mockReturnValue({
    left: 0, top: 0, width: 156, height: 156, right: 156, bottom: 156,
  });
}

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

  it('toggles closed again from the trigger', async () => {
    render(<ColorPicker value="#4f46e5" onChange={() => {}} label="Category color" />);
    const trigger = screen.getByRole('button', { name: /category color/i });

    await userEvent.click(trigger);
    await userEvent.click(trigger);

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('closes on a click outside', async () => {
    render(
      <div>
        <ColorPicker value="#4f46e5" onChange={() => {}} label="Category color" />
        <button type="button">Elsewhere</button>
      </div>
    );
    await userEvent.click(screen.getByRole('button', { name: /category color/i }));

    await userEvent.click(screen.getByRole('button', { name: 'Elsewhere' }));

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('stays open on a click inside the popover', async () => {
    render(<ColorPicker value="#4f46e5" onChange={() => {}} label="Category color" />);
    await userEvent.click(screen.getByRole('button', { name: /category color/i }));

    await userEvent.click(screen.getByLabelText(/category color hex value/i));

    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('ignores keys other than Escape', async () => {
    render(<ColorPicker value="#4f46e5" onChange={() => {}} label="Category color" />);
    await userEvent.click(screen.getByRole('button', { name: /category color/i }));

    await userEvent.keyboard('{Enter}');

    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('ignores a half-typed hex value', async () => {
    const onChange = vi.fn();
    render(<ColorPicker value="#4f46e5" onChange={onChange} label="Category color" />);
    await userEvent.click(screen.getByRole('button', { name: /category color/i }));

    const hex = screen.getByLabelText(/category color hex value/i);
    await userEvent.clear(hex);
    await userEvent.type(hex, '00ff');

    expect(onChange).not.toHaveBeenCalled();
    expect(hex).toHaveValue('00ff'); // still shows what was typed
  });

  it('accepts a hex without the leading hash and normalises it', async () => {
    const onChange = vi.fn();
    render(<ColorPicker value="#4f46e5" onChange={onChange} label="Category color" />);
    await userEvent.click(screen.getByRole('button', { name: /category color/i }));

    const hex = screen.getByLabelText(/category color hex value/i);
    await userEvent.clear(hex);
    await userEvent.type(hex, 'AABBCC');

    expect(onChange).toHaveBeenLastCalledWith('#aabbcc');
  });

  it('emits a new color when brightness is dragged', async () => {
    const onChange = vi.fn();
    render(<ColorPicker value="#4f46e5" onChange={onChange} label="Category color" />);
    await userEvent.click(screen.getByRole('button', { name: /category color/i }));

    fireEvent.change(screen.getByRole('slider'), { target: { value: '50' } });

    expect(onChange).toHaveBeenCalledWith(expect.stringMatching(/^#[0-9a-f]{6}$/));
  });

  it('picks a hue and saturation from a press on the wheel', async () => {
    const onChange = vi.fn();
    const { container } = render(
      <ColorPicker value="#4f46e5" onChange={onChange} label="Category color" />
    );
    await userEvent.click(screen.getByRole('button', { name: /category color/i }));

    const wheel = container.querySelector('.color-picker__wheel');
    measureWheel(wheel);

    // Straight right of centre at full radius: hue 90, saturation 1.
    fireEvent(wheel, pointer('pointerdown', { clientX: 156, clientY: 78 }));

    expect(onChange).toHaveBeenCalledWith(expect.stringMatching(/^#[0-9a-f]{6}$/));
  });

  it('tracks a drag across the wheel and stops on release', async () => {
    const onChange = vi.fn();
    const { container } = render(
      <ColorPicker value="#4f46e5" onChange={onChange} label="Category color" />
    );
    await userEvent.click(screen.getByRole('button', { name: /category color/i }));

    const wheel = container.querySelector('.color-picker__wheel');
    measureWheel(wheel);

    fireEvent(wheel, pointer('pointerdown', { clientX: 100, clientY: 78 }));
    fireEvent(wheel, pointer('pointermove', { clientX: 120, clientY: 78 }));
    const whileDragging = onChange.mock.calls.length;

    fireEvent(wheel, pointer('pointerup'));
    fireEvent(wheel, pointer('pointermove', { clientX: 140, clientY: 78 }));

    expect(whileDragging).toBe(2);
    expect(onChange).toHaveBeenCalledTimes(2); // the move after release is ignored
    expect(onChange).toHaveBeenLastCalledWith(expect.stringMatching(/^#[0-9a-f]{6}$/));
  });

  it('ignores a move that was never preceded by a press', async () => {
    const onChange = vi.fn();
    const { container } = render(
      <ColorPicker value="#4f46e5" onChange={onChange} label="Category color" />
    );
    await userEvent.click(screen.getByRole('button', { name: /category color/i }));

    fireEvent(container.querySelector('.color-picker__wheel'), pointer('pointermove', { clientX: 10, clientY: 10 }));

    expect(onChange).not.toHaveBeenCalled();
  });

  it('adopts a color chosen elsewhere', async () => {
    const { rerender } = render(
      <ColorPicker value="#4f46e5" onChange={() => {}} label="Category color" />
    );

    rerender(<ColorPicker value="#00ff00" onChange={() => {}} label="Category color" />);

    expect(screen.getByRole('button', { name: /category color: #00ff00/i })).toBeInTheDocument();
  });

  it('leaves the hex field alone while it is being typed in', async () => {
    const { rerender } = render(
      <ColorPicker value="#4f46e5" onChange={() => {}} label="Category color" />
    );
    await userEvent.click(screen.getByRole('button', { name: /category color/i }));
    const hex = screen.getByLabelText(/category color hex value/i);
    await userEvent.clear(hex);
    await userEvent.type(hex, '00ff00');

    // The parent echoes the committed value back while the field still has focus.
    rerender(<ColorPicker value="#00ff00" onChange={() => {}} label="Category color" />);

    expect(hex).toHaveValue('00ff00'); // not rewritten to '#00ff00' mid-edit
  });

  it('falls back to a sensible hue when the incoming value is not a color', () => {
    render(<ColorPicker value="not-a-color" onChange={() => {}} label="Category color" />);

    expect(screen.getByRole('button', { name: /category color: not-a-color/i })).toBeInTheDocument();
  });

  it('defaults its label', async () => {
    render(<ColorPicker value="#4f46e5" onChange={() => {}} />);

    expect(screen.getByRole('button', { name: /^color: #4f46e5$/i })).toBeInTheDocument();
  });

  it('accepts a hex typed with the leading hash', async () => {
    const onChange = vi.fn();
    render(<ColorPicker value="#4f46e5" onChange={onChange} label="Category color" />);
    await userEvent.click(screen.getByRole('button', { name: /category color/i }));

    const hex = screen.getByLabelText(/category color hex value/i);
    await userEvent.clear(hex);
    await userEvent.type(hex, '#00FF00');

    expect(onChange).toHaveBeenLastCalledWith('#00ff00');
  });
});
