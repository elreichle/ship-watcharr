import type { ReactNode } from 'react';
import { Navigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { BootScreen } from './BootScreen';

export function ProtectedRoute({ children }: { children: ReactNode }) {
  const { user, loading } = useAuth();

  if (loading) return <BootScreen />;
  if (!user) return <Navigate to="/login" replace />;

  return <>{children}</>;
}
