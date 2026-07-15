import { Link } from 'react-router-dom'
import { useAuth } from '../context/AuthContext'

const linkStyle = { color: '#8b5e3c', textDecoration: 'none', fontWeight: 600 }

export default function Footer() {
  const { user } = useAuth()

  return (
    <footer style={{
      borderTop: '1px solid #e8d5b0',
      padding: '20px',
      marginTop: 'auto',
      textAlign: 'center',
    }}>
      {/* Contact line is only useful once you're in the app — gate it to signed-in users. */}
      {user && (
        <p style={{ fontSize: 13, color: '#b0a090', margin: '0 0 6px' }}>
          Questions or feedback?{' '}
          <a href="mailto:madoncs522@gmail.com" style={linkStyle}>madoncs522@gmail.com</a>
          {' · '}
          <a href="https://www.linkedin.com/in/mason-scarborough-1a0667299/" target="_blank" rel="noopener noreferrer" style={linkStyle}>
            LinkedIn
          </a>
        </p>
      )}
      {/* Legal links ALWAYS render — required to be reachable before login. */}
      <p style={{ fontSize: 13, color: '#b0a090', margin: 0 }}>
        <Link to="/privacy" style={linkStyle}>Privacy Policy</Link>
        {' · '}
        <Link to="/terms" style={linkStyle}>Terms of Service</Link>
      </p>
    </footer>
  )
}
