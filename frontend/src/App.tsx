import { Navigate, Route, Routes } from 'react-router-dom';
import { AuthProvider } from './auth/AuthContext';
import { AppLayout } from './components/AppLayout';
import { ProtectedRoute } from './components/ProtectedRoute';
import { AdminRoute } from './components/AdminRoute';
import { LoginPage } from './pages/LoginPage';
import { RegisterPage } from './pages/RegisterPage';
import { WorksPage } from './pages/WorksPage';
import { WorkDetailPage } from './pages/WorkDetailPage';
import { ReaderPage } from './pages/ReaderPage';
import { NotificationsPage } from './pages/NotificationsPage';
import { FiltersPage } from './pages/FiltersPage';
import { ShipsPage } from './pages/ShipsPage';
import { DownloadsPage } from './pages/DownloadsPage';
import { StatsPage } from './pages/StatsPage';
import { SchedulesPage } from './pages/SchedulesPage';
import { AccountSettingsPage } from './pages/AccountSettingsPage';
import { AppearanceSettingsPage } from './pages/AppearanceSettingsPage';
import { DownloadSettingsPage } from './pages/DownloadSettingsPage';
import { Ao3SettingsPage } from './pages/Ao3SettingsPage';
import { AdminDatabasePage } from './pages/AdminDatabasePage';

export default function App() {
  return (
    <AuthProvider>
      <Routes>
        {/* Auth pages render without the shell — there is nothing to navigate to yet. */}
        <Route path="/login" element={<LoginPage />} />
        <Route path="/register" element={<RegisterPage />} />

        <Route
          element={
            <ProtectedRoute>
              <AppLayout />
            </ProtectedRoute>
          }
        >
          {/* The dashboard is a group of sibling views, so "/" is a redirect rather than a page
              of its own. Works leads because it is the thing the app is for. */}
          <Route path="/" element={<Navigate to="/works" replace />} />
          <Route path="/works" element={<WorksPage key="works" />} />
          {/* The same table over the reader's favorites. A view of the works page rather than a
              page of its own, so a row is marked, rated and noted the same way in both places.
              Keyed apart so switching tabs starts a fresh page instead of repainting the old
              rows under the new heading while the new ones load. */}
          <Route path="/favorites" element={<WorksPage key="favorites" favorites />} />
          {/* A work of its own, under the list it is reached from, so the sidebar keeps Works lit. */}
          <Route path="/works/:workId" element={<WorkDetailPage />} />
          {/* The work's EPUB, read in the app. Under the work for the same reason its page is. */}
          <Route path="/works/:workId/read" element={<ReaderPage />} />
          <Route path="/notifications" element={<NotificationsPage />} />
          <Route path="/filters" element={<FiltersPage />} />
          <Route path="/ships" element={<ShipsPage />} />
          <Route path="/downloads" element={<DownloadsPage />} />
          <Route path="/stats" element={<StatsPage />} />
          <Route path="/schedules" element={<SchedulesPage />} />

          <Route path="/settings" element={<Navigate to="/settings/account" replace />} />
          <Route path="/settings/account" element={<AccountSettingsPage />} />
          <Route path="/settings/appearance" element={<AppearanceSettingsPage />} />
          <Route path="/settings/downloads" element={<DownloadSettingsPage />} />

          <Route
            path="/system/ao3"
            element={
              <AdminRoute>
                <Ao3SettingsPage />
              </AdminRoute>
            }
          />
          <Route
            path="/system/database"
            element={
              <AdminRoute>
                <AdminDatabasePage />
              </AdminRoute>
            }
          />

          {/* Admin pages moved under /system to match the sidebar's Settings/System split. */}
          <Route path="/admin/scraping" element={<Navigate to="/system/ao3" replace />} />
          <Route path="/system/scraping" element={<Navigate to="/system/ao3" replace />} />
          <Route path="/admin/database" element={<Navigate to="/system/database" replace />} />
        </Route>

        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </AuthProvider>
  );
}
