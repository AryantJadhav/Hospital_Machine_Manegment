import { useCallback, useEffect, useState } from 'react';
import { Navigate, useLocation, useNavigate, useParams } from 'react-router-dom';
import { api } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { ROLES } from '../auth/context';
import { PmChecklistForm } from './PmChecklistForm';
import { PmDonePage } from './PmDonePage';
import type { RecordPm } from './PmDonePage';

/**
 * Doing one PM, on its own address: /pm/:taskId/do.
 *
 * The form used to live inside the PM list, so the only way to reach a machine's
 * PM was to find its row. A technician who has scanned a tag, or is looking at a
 * machine's own page and can see "Maintenance due", could not start it from
 * there. Now anywhere can link here, and this sends them back to where they came
 * from - the machine's page shows the new entry in its history, the PM list
 * keeps its filters and its place.
 *
 * Most PMs have no checklist: they are recorded as done, with who did it, when and the
 * report if there is one, on a page of their own. Only a PM scheduled with a checklist,
 * from the Checklists page, is filled in question by question.
 */
export function PmDoPage() {
  const { taskId } = useParams();
  const navigate = useNavigate();
  const location = useLocation();
  const { can } = useAuth();

  const from = (location.state as { from?: string } | null)?.from ?? '/pm';
  const id = Number(taskId);
  const valid = Number.isInteger(id) && id > 0;

  // The answer, and which PM it is the answer for. undefined while it is asked, and for
  // as long as it is the answer for a different PM; null when it has a checklist to fill in,
  // or could not be asked, in which case the checklist form is shown and says what is wrong
  // if anything is.
  const [answer, setAnswer] = useState<{ id: number; record: RecordPm | null } | null>(null);
  const record = answer?.id === id ? answer.record : undefined;

  const load = useCallback(async () => {
    try {
      const r = await api.get<RecordPm | { simple: false }>(`/api/pm/tasks/${id}/record`);
      setAnswer({ id, record: r.simple ? r : null });
    } catch {
      setAnswer({ id, record: null });
    }
  }, [id]);

  useEffect(() => {
    if (valid) void load();
  }, [valid, load]);

  if (!valid) return <Navigate to="/pm" replace />;

  const back = () => navigate(from);
  const done = (
    message: string,
    completed: boolean,
    task: { id: number; assetTag: string; dueDate: string },
  ) =>
    navigate(from, {
      replace: true,
      state: { handoff: { notice: message, recorded: completed ? task : null } },
    });

  if (record === undefined) return <div className="page"><p className="muted">Loading…</p></div>;

  if (record) {
    return (
      <PmDonePage
        key={id}
        data={record}
        canAdminister={can(ROLES.admin)}
        backLabel={from.startsWith('/equipment') ? 'the machine' : 'the PM list'}
        onClose={back}
        onDone={done}
        onChanged={load}
      />
    );
  }

  return (
    <PmChecklistForm
      // A fresh form per PM. Without it, changing the address from one task to
      // another reused the same form, which kept showing the first PM's questions
      // - and, for a task that does not exist, never noticed it was gone.
      key={id}
      taskId={id}
      // Deciding a PM will not happen is a supervisory call, not a technician's.
      // The server enforces the same rule; this only keeps the button out of the way.
      canSkip={can(ROLES.admin)}
      backLabel={from.startsWith('/equipment') ? 'the machine' : 'the PM list'}
      onClose={back}
      onDone={done}
    />
  );
}
