import { useEffect, useState } from 'react';
import { BrowserRouter, Navigate, NavLink, Route, Routes } from 'react-router-dom';
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
import { ScanPage } from './pages/ScanPage';
import { DashboardPage } from './pages/DashboardPage';
import { PmTasksPage } from './pages/PmTasksPage';
import { WorkOrdersPage } from './pages/WorkOrdersPage';
import './App.css';

function Shell() {
  const { user, logout, can } = useAuth();
  const canImport = can(ROLES.admin, ROLES.biomedicalHead);
  const isAdmin = can(ROLES.admin);

  return (
    <div className="shell">
      <nav className="nav">
        <span className="brand">Hospital PM</span>

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

        {canImport && (
          <NavLink to="/import" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
            Import
          </NavLink>
        )}

        {isAdmin && (
          <NavLink to="/backups" className={({ isActive }) => (isActive ? 'nav-link active' : 'nav-link')}>
            Backups
          </NavLink>
        )}

        <div className="nav-right">
          <span className="muted">{user?.fullName ?? user?.userName}</span>
          <button className="btn btn-quiet" onClick={() => void logout()}>Sign out</button>
        </div>
      </nav>

      <main>
        <Routes>
          <Route path="/dashboard" element={<DashboardPage />} />
          <Route path="/pm" element={<PmTasksPage />} />
          <Route path="/work-orders" element={<WorkOrdersPage />} />
          <Route path="/equipment" element={<EquipmentListPage />} />
          <Route path="/equipment/:id" element={<EquipmentDetailPage />} />
          <Route path="/locations" element={<LocationsPage />} />
          <Route path="/scan" element={<ScanPage />} />
          <Route
            path="/import"
            element={canImport ? <ImportPage /> : <Navigate to="/dashboard" replace />}
          />
          <Route
            path="/backups"
            element={isAdmin ? <BackupsPage /> : <Navigate to="/dashboard" replace />}
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
