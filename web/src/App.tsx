import { useEffect, useState } from 'react';
import { BrowserRouter, Navigate, NavLink, Route, Routes, useLocation } from 'react-router-dom';
import { api } from './api/client';
import { AuthProvider } from './auth/AuthContext';
import { useAuth } from './auth/useAuth';
import { ROLES } from './auth/context';
import { LoginPage } from './pages/LoginPage';
import { EquipmentListPage } from './pages/EquipmentListPage';
import { EquipmentDetailPage } from './pages/EquipmentDetailPage';
import { ImportPage } from './pages/ImportPage';
import { SetupPage } from './pages/SetupPage';
import { LocationsPage } from './pages/LocationsPage';
import { BackupsPage } from './pages/BackupsPage';
import { DiagnosticsPage } from './pages/DiagnosticsPage';
import { LicencePage } from './pages/LicencePage';
import { UpdatesPage } from './pages/UpdatesPage';
import { ScanPage } from './pages/ScanPage';
import { DashboardPage } from './pages/DashboardPage';
import { PmTasksPage } from './pages/PmTasksPage';
import { PmDoPage } from './pages/PmDoPage';
import { StaffPage } from './pages/StaffPage';
import { ChecklistsPage } from './pages/ChecklistsPage';
import { WorkOrdersPage } from './pages/WorkOrdersPage';
import { AdminMenu } from './AdminMenu';
import { titleForPath, usePageTitle } from './pageTitle';
import './App.css';

function Shell() {
  const { user, logout, can, signInNotice, dismissNotice } = useAuth();
  // One flag, because there is one line: an Employee records what they did,
  // an Admin decides what gets done. Everything hidden below is a decision
  // about the department rather than a record of a job.
  const isAdmin = can(ROLES.admin);
  const { pathname } = useLocation();
  usePageTitle(titleForPath(pathname));

  return (
    <div className="shell">
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
            Work orders
          </NavLink>

          <NavLink to="/equipment" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
            Equipment
          </NavLink>

          {/* Hidden rather than shown-and-rejected. The server enforces the
              same rule, so this is presentation, not the access control. */}
          <NavLink to="/scan" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
            Scan
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
        </div>

        {isAdmin && <AdminMenu />}

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
          <button
            className="btn btn-quiet"
            title={`Sign out ${user?.fullName ?? user?.userName ?? ''}`.trim()}
            onClick={() => void logout()}
          >
            Sign out
          </button>
        </div>
      </nav>

      <main>
        {/* The login screen's chooser disagreed with the account. Said
            once, here rather than there, because the sign-in has already
            succeeded by the time it is known. */}
        {signInNotice && (
          <div className="page">
            <p className="alert alert-info notice-row" role="status">
              <span>{signInNotice}</span>
              <button className="btn btn-quiet" onClick={dismissNotice}>Dismiss</button>
            </p>
          </div>
        )}

        <Routes>
          <Route path="/dashboard" element={<DashboardPage />} />
          <Route path="/pm" element={<PmTasksPage />} />
          <Route path="/pm/:taskId/do" element={<PmDoPage />} />
          <Route path="/work-orders" element={<WorkOrdersPage />} />
          <Route path="/equipment" element={<EquipmentListPage />} />
          <Route path="/equipment/:id" element={<EquipmentDetailPage />} />
          <Route path="/locations" element={<LocationsPage />} />
          <Route path="/checklists" element={<ChecklistsPage />} />
          <Route path="/scan" element={<ScanPage />} />
          <Route
            path="/import"
            element={isAdmin ? <ImportPage /> : <Navigate to="/dashboard" replace />}
          />
          <Route
            path="/staff"
            element={isAdmin ? <StaffPage /> : <Navigate to="/dashboard" replace />}
          />

          <Route
            path="/backups"
            element={isAdmin ? <BackupsPage /> : <Navigate to="/dashboard" replace />}
          />
          <Route
            path="/diagnostics"
            element={isAdmin ? <DiagnosticsPage /> : <Navigate to="/dashboard" replace />}
          />
          <Route
            path="/licence"
            element={isAdmin ? <LicencePage /> : <Navigate to="/dashboard" replace />}
          />
          <Route
            path="/updates"
            element={isAdmin ? <UpdatesPage /> : <Navigate to="/dashboard" replace />}
          />
          <Route path="*" element={<Navigate to="/dashboard" replace />} />
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
