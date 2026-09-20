import { Navigate, useLocation, useNavigate, useParams } from 'react-router-dom';
import { useAuth } from '../auth/useAuth';
import { ROLES } from '../auth/context';
import { PmChecklistForm } from './PmChecklistForm';

/**
 * Doing one PM, on its own address: /pm/:taskId/do.
 *
 * The form used to live inside the PM list, so the only way to reach a machine's
 * PM was to find its row. A technician who has scanned a tag, or is looking at a
 * machine's own page and can see "Maintenance due", could not start it from
 * there. Now anywhere can link here, and this sends them back to where they came
 * from - the machine's page shows the new entry in its history, the PM list
 * keeps its filters and its place.
 */
export function PmDoPage() {
  const { taskId } = useParams();
  const navigate = useNavigate();
  const location = useLocation();
  const { can } = useAuth();

  const from = (location.state as { from?: string } | null)?.from ?? '/pm';
  const id = Number(taskId);

  if (!Number.isInteger(id) || id <= 0) return <Navigate to="/pm" replace />;

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
      onClose={() => navigate(from)}
      onDone={(message, completed, task) =>
        navigate(from, {
          replace: true,
          state: { handoff: { notice: message, recorded: completed ? task : null } },
        })
      }
    />
  );
}
