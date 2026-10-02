import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { BrowserRouter, Navigate, NavLink, Route, Routes, useLocation } from 'react-router-dom';
import { api } from './api/client';
import { AuthProvider } from './auth/AuthContext';
import { useAuth } from './auth/useAuth';
import { PERMISSIONS, ROLES } from './auth/context';
import { LoginPage } from './pages/LoginPage';
import { EquipmentListPage } from './pages/EquipmentListPage';
import { EquipmentDetailPage } from './pages/EquipmentDetailPage';
import { EquipmentTypesPage } from './pages/EquipmentTypesPage';
import { SparePartsPage } from './pages/SparePartsPage';
import { ImportPage } from './pages/ImportPage';
import { SetupPage } from './pages/SetupPage';
import { LocationsPage } from './pages/LocationsPage';
import { BackupsPage } from './pages/BackupsPage';
import { DiagnosticsPage } from './pages/DiagnosticsPage';
import { LicencePage } from './pages/LicencePage';
import { UpdatesPage } from './pages/UpdatesPage';
import { DashboardPage } from './pages/DashboardPage';
import { PmTasksPage } from './pages/PmTasksPage';
import { PmDoPage } from './pages/PmDoPage';
import { StaffPage } from './pages/StaffPage';
import { CompliancePage } from './pages/CompliancePage';
import { ReportsPage } from './pages/ReportsPage';
import { ExportPage } from './pages/ExportPage';
import { ChecklistsPage } from './pages/ChecklistsPage';
import { ServiceReportPreviewPage } from './pages/ServiceReportPreviewPage';
import { TrainingPage } from './pages/TrainingPage';
import { TrainingReportPreviewPage } from './pages/TrainingReportPreviewPage';
import { TrainingSessionPage } from './pages/TrainingSessionPage';
import { WorkOrderPage } from './pages/WorkOrderPage';
import { WorkOrdersPage } from './pages/WorkOrdersPage';
import { AdminMenu } from './AdminMenu';
import { LicenceBanner } from './LicenceBanner';
import { NotificationBell } from './NotificationBell';
import { ThemeToggle } from './ThemeToggle';
import { useFeatures } from './features';
import { titleForPath, usePageTitle } from './pageTitle';
import './App.css';

const ADMIN_ONLY = 'That page is for administrators.';
const SWITCHED_OFF = 'That part of the system is switched off.';
const NO_SUCH_PAGE = 'There is no page at that address.';

/**
 * Where someone is sent when the page they asked for is not theirs, or not
 * there. It says so on arrival: bouncing to the dashboard with nothing said
 * looked like the link was broken.
 */
function Elsewhere({ notice }: { notice: string }) {
  return <Navigate to="/dashboard" replace state={{ handoff: { notice, recorded: null, tone: 'info' } }} />;
}

function Shell() {
  const { user, logout, can, may, signInNotice, dismissNotice } = useAuth();
  // Only for the name shown beside the account. What a page or a button offers is decided by
  // what the person may do (`may`), never by which role they hold.
  const isAdmin = can(ROLES.admin);
  // Import, export, backups and updates can be switched off for everyone. Null until they are known.
  const features = useFeatures();
  const gated = (permission: string, on: boolean | undefined, page: ReactNode) =>
    !may(permission) ? <Elsewhere notice={ADMIN_ONLY} />
      : features === null ? <div className="page"><p className="muted">Loading…</p></div>
        : on ? page : <Elsewhere notice={SWITCHED_OFF} />;
  const { pathname } = useLocation();
  usePageTitle(titleForPath(pathname));

  return (
    <div className="shell">
      {/* First thing a keyboard user reaches: past the dozen menu links, to the page itself. */}
      <a className="skip-link" href="#main">Skip to the page</a>
      <nav className="nav">
        <span className="brand">Hospital PM</span>

        <div className="nav-links">
          <NavLink to="/dashboard" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
            Today
          </NavLink>

          <NavLink to="/pm" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
            PM
          </NavLink>

          <NavLink to="/work-orders" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
            Request Service
          </NavLink>

          <NavLink to="/equipment" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
            Equipment
          </NavLink>

          {/* Visible to everyone, editable only by an Admin, the same split as
              Checklists: an engineer checking the shelf before promising a
              repair date is the reason this page exists. */}
          <NavLink to="/spare-parts" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
            Spare parts
          </NavLink>

          <NavLink to="/locations" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
            Locations
          </NavLink>

          {/* Visible to everyone, editable only by an Admin. Someone reading
              the checklist they are about to work from is reasonable; the
              server enforces who may change it. */}
          <NavLink to="/checklists" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
            Checklists
          </NavLink>

          {/* Visible to everyone, editable only by an Admin: who has been trained on what is a
              question anyone handing a machine over wants answered. */}
          <NavLink to="/training" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
            Training
          </NavLink>
        </div>

        <AdminMenu features={features} />

        <div className="nav-right">
          {/* The role is shown next to the name. An Employee who cannot find
              the Staff tab should be able to see why without asking. */}
          <span className="muted">
            {user?.fullName ?? user?.userName}
            <span className="nav-role">
              {' · '}
              {isAdmin ? 'Administrator' : 'Employee'}
            </span>
          </span>
          {/* For everyone signed in, not only an Administrator: a PM is the department's work. */}
          <NotificationBell />
          <ThemeToggle />
          <button
            className="btn btn-quiet"
            title={`Sign out ${user?.fullName ?? user?.userName ?? ''}`.trim()}
            onClick={() => void logout()}
          >
            Sign out
          </button>
        </div>
      </nav>

      <main id="main" tabIndex={-1}>
        {/* The login screen's chooser disagreed with the account. Said
            once, here rather than there, because the sign-in has already
            succeeded by the time it is known. */}
        <LicenceBanner />

        {signInNotice && (
          <div className="page">
            <p className="alert alert-info notice-row" role="status">
              <span>{signInNotice}</span>
              <button className="btn btn-quiet" onClick={dismissNotice}>Dismiss</button>
            </p>
          </div>
        )}

        <Routes>
          <Route path="/" element={<Navigate to="/dashboard" replace />} />
          <Route path="/dashboard" element={<DashboardPage />} />
          <Route path="/pm" element={<PmTasksPage />} />
          <Route path="/pm/:taskId/do" element={<PmDoPage />} />
          <Route path="/training" element={<TrainingPage />} />
          <Route path="/training/:id" element={<TrainingSessionPage />} />
          <Route path="/training/:id/report" element={<TrainingReportPreviewPage />} />
          <Route path="/work-orders" element={<WorkOrdersPage />} />
          <Route path="/work-orders/:id" element={<WorkOrderPage />} />
          <Route path="/work-orders/:id/report" element={<ServiceReportPreviewPage />} />
          <Route path="/equipment" element={<EquipmentListPage />} />
          <Route path="/equipment/:id" element={<EquipmentDetailPage />} />
          <Route path="/locations" element={<LocationsPage />} />
          <Route path="/checklists" element={<ChecklistsPage />} />
          <Route path="/spare-parts" element={<SparePartsPage />} />
          <Route
            path="/reports"
            element={may(PERMISSIONS.reportsView) ? <ReportsPage /> : <Elsewhere notice={ADMIN_ONLY} />}
          />
          <Route
            path="/compliance"
            element={may(PERMISSIONS.reportsView) ? <CompliancePage /> : <Elsewhere notice={ADMIN_ONLY} />}
          />
          <Route path="/export" element={gated(PERMISSIONS.dataExport, features?.export, <ExportPage />)} />
          <Route
            path="/equipment-types"
            element={may(PERMISSIONS.equipmentTypesEdit) ? <EquipmentTypesPage /> : <Elsewhere notice={ADMIN_ONLY} />}
          />
          <Route path="/import" element={gated(PERMISSIONS.dataImport, features?.import, <ImportPage />)} />
          <Route
            path="/staff"
            element={may(PERMISSIONS.staffManage) ? <StaffPage /> : <Elsewhere notice={ADMIN_ONLY} />}
          />

          <Route path="/backups" element={gated(PERMISSIONS.systemBackups, features?.backups, <BackupsPage />)} />
          <Route
            path="/diagnostics"
            element={may(PERMISSIONS.systemDiagnostics) ? <DiagnosticsPage /> : <Elsewhere notice={ADMIN_ONLY} />}
          />
          <Route
            path="/licence"
            element={may(PERMISSIONS.systemLicence) ? <LicencePage /> : <Elsewhere notice={ADMIN_ONLY} />}
          />
          <Route path="/updates" element={gated(PERMISSIONS.systemUpdates, features?.updates, <UpdatesPage />)} />
          <Route path="*" element={<Elsewhere notice={NO_SUCH_PAGE} />} />
        </Routes>
      </main>
    </div>
  );
}

function Gate() {
  const { user, loading } = useAuth();
  const [needsSetup, setNeedsSetup] = useState<boolean | null>(null);
  // Once signed in the shell names the page; before that, this names the screen.
  usePageTitle(user ? undefined : needsSetup ? 'Set up' : 'Sign in');

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const s = await api.get<{ needsSetup: boolean }>('/api/setup/status');
        if (!cancelled) setNeedsSetup(s.needsSetup);
      } catch {
        // If the check fails, fall through to the login form rather than
        // stranding the user on a blank screen.
        if (!cancelled) setNeedsSetup(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  // Blank while the stored refresh token is exchanged and the setup state is
  // read, so reopening a tablet mid-shift does not flash the login form
  // before landing on the register.
  if (loading || needsSetup === null) return <div className="boot">Loading…</div>;

  if (user) return <Shell />;

  return needsSetup ? <SetupPage onDone={() => setNeedsSetup(false)} /> : <LoginPage />;
}

export default function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        <Gate />
      </BrowserRouter>
    </AuthProvider>
  );
}
