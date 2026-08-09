import { Link, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';

export function NavBar() {
  const { user, logout } = useAuth();
  const navigate = useNavigate();

  if (!user) return null;

  const onLogout = async () => {
    await logout();
    navigate('/login');
  };

  return (
    <nav className="navbar">
      <Link to="/">Dashboard</Link>
      <Link to="/settings">Account settings</Link>
      {user.isAdmin && <Link to="/admin/scraping">Scraping</Link>}
      {user.isAdmin && <Link to="/admin/database">Database</Link>}
      <span className="spacer" />
      <span>{user.email}</span>
      <button onClick={onLogout}>Log out</button>
    </nav>
  );
}
