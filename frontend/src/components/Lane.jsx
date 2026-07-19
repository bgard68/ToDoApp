import { useState } from 'react';
import TaskCard from './TaskCard.jsx';

const laneKey = { 0: 'todo', 1: 'progress', 2: 'done' };

/** A Kanban column that accepts dropped cards and changes their status. */
export default function Lane({ status, label, todos, categories, onDropCard, onDragStart, onDragEnd, onUpdate, onDelete }) {
  const [over, setOver] = useState(false);

  function handleDragOver(e) {
    e.preventDefault(); // required to allow a drop
    e.dataTransfer.dropEffect = 'move';
    if (!over) setOver(true);
  }

  function handleDrop(e) {
    e.preventDefault();
    setOver(false);
    const id = Number(e.dataTransfer.getData('text/plain'));
    if (id) onDropCard(id, status);
  }

  return (
    <section
      className={`lane lane--${laneKey[status]} ${over ? 'is-over' : ''}`}
      onDragOver={handleDragOver}
      onDragLeave={() => setOver(false)}
      onDrop={handleDrop}
    >
      <header className="lane__header">
        <h2>{label}</h2>
        <span className="lane__count">{todos.length}</span>
      </header>
      <div className="lane__cards">
        {todos.length === 0 ? (
          <p className="lane__empty">Drop tasks here</p>
        ) : (
          todos.map((t) => (
            <TaskCard
              key={t.id}
              todo={t}
              categories={categories}
              onUpdate={onUpdate}
              onDelete={onDelete}
              onMove={onDropCard}
              onDragStart={onDragStart}
              onDragEnd={onDragEnd}
            />
          ))
        )}
      </div>
    </section>
  );
}
