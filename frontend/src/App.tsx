import { Navigate, Route, Routes } from 'react-router-dom';
import { AuthProvider } from './auth/AuthContext';
import { AppLayout } from './components/AppLayout';
import { ProtectedRoute } from './components/ProtectedRoute';
import { AdminRoute } from './components/AdminRoute';
import { LoginPage } from './pages/LoginPage';
import { RegisterPage } from './pages/RegisterPage';
import { DashboardPage } from './pages/DashboardPage';
import { AccountSettingsPage } from './pages/AccountSettingsPage';
import { AppearanceSettingsPage } from './pages/AppearanceSettingsPage';
import { AdminScrapingPage } from './pages/AdminScrapingPage';
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
          <Route path="/" element={<DashboardPage />} />

          <Route path="/settings" element={<Navigate to="/settings/account" replace />} />
          <Route path="/settings/account" element={<AccountSettingsPage />} />
          <Route path="/settings/appearance" element={<AppearanceSettingsPage />} />

          <Route
            path="/system/scraping"
            element={
              <AdminRoute>
                <AdminScrapingPage />
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
          <Route path="/admin/scraping" element={<Navigate to="/system/scraping" replace />} />
          <Route path="/admin/database" element={<Navigate to="/system/database" replace />} />
        </Route>

        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </AuthProvider>
  );
}
