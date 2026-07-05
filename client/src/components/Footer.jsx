import { useAuth } from '../context/AuthContext'

export default function Footer() {
  const { user } = useAuth()
  if (!user) return null

  return (
    <footer style={{
      borderTop: '1px solid #e8d5b0',
      padding: '20px',
      marginTop: 'auto',
      textAlign: 'center',
    }}>
      <p style={{ fontSize: 13, color: '#b0a090', margin: 0 }}>
        Questions or feedback?{' '}
        <a href="mailto:madoncs522@gmail.com" style={{ color: '#8b5e3c', textDecoration: 'none', fontWeight: 600 }}>
          madoncs522@gmail.com
        </a>
        {' · '}
        <a
          href="https://www.linkedin.com/in/mason-scarborough-1a0667299/"
          target="_blank"
          rel="noopener noreferrer"
          style={{ color: '#8b5e3c', textDecoration: 'none', fontWeight: 600 }}
        >
          LinkedIn
        </a>
      </p>
    </footer>
  )
}
