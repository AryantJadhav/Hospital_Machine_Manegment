import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { BrowserRouter, Navigate, NavLink, Route, Routes, useLocation } from 'react-router-dom';
import { api } from './api/client';
import { AuthProvider } from './auth/AuthContext';
import { ToastProvider } from './toast';
import { useAuth } from './auth/useAuth';
import { PERMISSIONS, ROLE_LABEL } from './auth/context';
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
import { AccessPage } from './pages/AccessPage';
import { AuditPage } from './pages/AuditPage';
import { ChecklistsPage } from './pages/ChecklistsPage';
import { GatePassesPage } from './pages/GatePassesPage';
import { GatePassNewPage } from './pages/GatePassForm';
import { GatePassPage } from './pages/GatePassPage';
import { GatePassPreviewPage } from './pages/GatePassPreviewPage';
import { ServiceHistoryPage } from './pages/ServiceHistoryPage';
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

const ADMIN_ONLY = 'That page is not part of your access.';
const SWITCHED_OFF = 'That part of the system is switched off.';
const NO_SUCH_PAGE = 'There is no page at that address.';

/**
 * Where someone is sent when the page they asked for is not theirs, or not
 * there: back to their own starting page. It says so on arrival: bouncing there
 * with nothing said looked like the link was broken.
 */
function Elsewhere({ notice }: { notice: string }) {
  return <Navigate to="/" replace state={{ handoff: { notice, recorded: null, tone: 'info' } }} />;
}

/**
 * Where a person starts. The day's work for someone who works with the equipment; the staff
 * list for the hospital's IT team, who keep the installation running and see none of it; and a
 * plain word for an account that has been given nothing yet.
 */
function Landing() {
  const { may } = useAuth();
  const { state } = useLocation();
  if (may(PERMISSIONS.registerView)) return <Navigate to="/dashboard" replace state={state} />;
  // A person from another department starts at their own service requests.
  if (may(PERMISSIONS.workOrdersView)) return <Navigate to="/work-orders" replace state={state} />;
  if (may(PERMISSIONS.staffManage)) return <Navigate to="/staff" replace state={state} />;
  return (
    <div className="page stack">
      <h1>Welcome</h1>
      <p className="muted">
        Your account is set up, but it has not been given access to anything yet. Ask the Head of
        Biomedical, or the hospital&apos;s IT team, to give you access.
      </p>
    </div>
  );
}

function Shell() {
  const { user, logout, may } = useAuth();
  // The role is only for the name shown beside the account. What a page or a button offers is
  // decided by what the person may do (`may`), never by which role they hold.
  const roleName = ROLE_LABEL[user?.roles[0] ?? ''] ?? '';
  // A page for people who may use it; anyone else is sent to their own starting page.
  const needs = (permission: string, page: ReactNode) =>
    may(permission) ? page : <Elsewhere notice={ADMIN_ONLY} />;
  const needsAny = (permissions: string[], page: ReactNode) =>
    may(...permissions) ? page : <Elsewhere notice={ADMIN_ONLY} />;
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
          {may(PERMISSIONS.registerView) && (
            <NavLink to="/dashboard" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
              Today
            </NavLink>
          )}

          {may(PERMISSIONS.pmWork) && (
            <NavLink to="/pm" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
              PM
            </NavLink>
          )}

          {may(PERMISSIONS.workOrdersView) && (
            <NavLink to="/work-orders" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
              Request Service
            </NavLink>
          )}

          {/* For a person from another department: the repairs done on their machines. Those who work
              on the equipment have the full lists and the Reports pages instead. */}
          {may(PERMISSIONS.workOrdersView) && !may(PERMISSIONS.registerView) && (
            <NavLink to="/service-history" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
              Service history
            </NavLink>
          )}

          {may(PERMISSIONS.registerView, PERMISSIONS.departmentView) && (
            <NavLink to="/equipment" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
              Equipment
            </NavLink>
          )}

          {/* Visible to everyone, editable only by an Admin, the same split as
              Checklists: an engineer checking the shelf before promising a
              repair date is the reason this page exists. */}
          {may(PERMISSIONS.sparePartsView) && (
            <NavLink to="/spare-parts" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
              Spare parts
            </NavLink>
          )}

          {may(PERMISSIONS.registerView) && (
            <NavLink to="/locations" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
              Locations
            </NavLink>
          )}

          {/* Visible to everyone, editable only by an Admin. Someone reading
              the checklist they are about to work from is reasonable; the
              server enforces who may change it. */}
          {may(PERMISSIONS.checklistsView) && (
            <NavLink to="/checklists" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
              Checklists
            </NavLink>
          )}

          {/* Machines sent out of the hospital to a vendor for repair, and when they are due back. */}
          {may(PERMISSIONS.gatePassView) && (
            <NavLink to="/gate-passes" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
              Gate passes
            </NavLink>
          )}

          {/* Visible to everyone, editable only by an Admin: who has been trained on what is a
              question anyone handing a machine over wants answered. */}
          {may(PERMISSIONS.trainingView) && (
            <NavLink to="/training" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
              Training
            </NavLink>
          )}
        </div>

        <AdminMenu features={features} />

        <div className="nav-right">
          {/* The role is shown next to the name. An Employee who cannot find
              the Staff tab should be able to see why without asking. */}
          <span className="muted">
            {user?.fullName ?? user?.userName}
            <span className="nav-role">
              {' · '}
              {roleName}
              {(user?.departments.length ?? 0) > 0 && ` (${user?.departments.join(', ')})`}
            </span>
          </span>
          {/* For whoever does PM work: the reminders are about the department's PMs. */}
          {may(PERMISSIONS.pmWork) && <NotificationBell />}
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

        <Routes>
          <Route path="/" element={<Landing />} />
          <Route path="/dashboard" element={needs(PERMISSIONS.registerView, <DashboardPage />)} />
          <Route path="/pm" element={needs(PERMISSIONS.pmWork, <PmTasksPage />)} />
          <Route path="/pm/:taskId/do" element={needs(PERMISSIONS.pmWork, <PmDoPage />)} />
          <Route path="/training" element={needs(PERMISSIONS.trainingView, <TrainingPage />)} />
          <Route path="/training/:id" element={needs(PERMISSIONS.trainingView, <TrainingSessionPage />)} />
          <Route path="/training/:id/report" element={needs(PERMISSIONS.trainingView, <TrainingReportPreviewPage />)} />
          <Route path="/gate-passes" element={needs(PERMISSIONS.gatePassView, <GatePassesPage />)} />
          <Route path="/gate-passes/new" element={needs(PERMISSIONS.gatePassEdit, <GatePassNewPage />)} />
          <Route path="/gate-passes/:id" element={needs(PERMISSIONS.gatePassView, <GatePassPage />)} />
          <Route path="/gate-passes/:id/pdf" element={needs(PERMISSIONS.gatePassView, <GatePassPreviewPage />)} />
          <Route path="/work-orders" element={needs(PERMISSIONS.workOrdersView, <WorkOrdersPage />)} />
          <Route path="/service-history" element={needs(PERMISSIONS.workOrdersView, <ServiceHistoryPage />)} />
          <Route path="/work-orders/:id" element={needs(PERMISSIONS.registerView, <WorkOrderPage />)} />
          <Route path="/work-orders/:id/report" element={needs(PERMISSIONS.workOrdersView, <ServiceReportPreviewPage />)} />
          <Route path="/equipment" element={needsAny([PERMISSIONS.registerView, PERMISSIONS.departmentView], <EquipmentListPage />)} />
          {/* The pages of one machine and of one request are for those who work on them. A person from
              another department sees them as rows in a list and opens nothing: they hold no
              register.view, which is what is asked for here. */}
          <Route path="/equipment/:id" element={needs(PERMISSIONS.registerView, <EquipmentDetailPage />)} />
          <Route path="/locations" element={needs(PERMISSIONS.registerView, <LocationsPage />)} />
          <Route path="/checklists" element={needs(PERMISSIONS.checklistsView, <ChecklistsPage />)} />
          <Route path="/spare-parts" element={needs(PERMISSIONS.sparePartsView, <SparePartsPage />)} />
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

          <Route
            path="/audit"
            element={may(PERMISSIONS.auditView) ? <AuditPage /> : <Elsewhere notice={ADMIN_ONLY} />}
          />

          <Route
            path="/access"
            element={may(PERMISSIONS.accessManage) ? <AccessPage /> : <Elsewhere notice={ADMIN_ONLY} />}
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
      <ToastProvider>
        <BrowserRouter>
          <Gate />
        </BrowserRouter>
      </ToastProvider>
    </AuthProvider>
  );
}
