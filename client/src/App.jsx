import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { AuthProvider } from './context/AuthContext'
import ProtectedRoute from './components/ProtectedRoute'
import Navbar from './components/Navbar'
import Footer from './components/Footer'
import Login from './pages/Login'
import Dashboard from './pages/Dashboard'
import TripNew from './pages/TripNew'
import TripDetail from './pages/TripDetail'
import TransactionReview from './pages/TransactionReview'
import ConnectBank from './pages/ConnectBank'
import Account from './pages/Account'

export default function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        <div style={{ display: 'flex', flexDirection: 'column', minHeight: '100vh' }}>
          <Navbar />
          <div style={{ flex: 1 }}>
            <Routes>
              <Route path="/" element={<Navigate to="/login" replace />} />
              <Route path="/login" element={<Login />} />
              <Route path="/dashboard" element={<ProtectedRoute><Dashboard /></ProtectedRoute>} />
              <Route path="/trips/new" element={<ProtectedRoute><TripNew /></ProtectedRoute>} />
              <Route path="/trips/:id" element={<ProtectedRoute><TripDetail /></ProtectedRoute>} />
              <Route path="/trips/:id/review" element={<ProtectedRoute><TransactionReview /></ProtectedRoute>} />
              <Route path="/connect-bank" element={<ProtectedRoute><ConnectBank /></ProtectedRoute>} />
              <Route path="/account" element={<ProtectedRoute><Account /></ProtectedRoute>} />
            </Routes>
          </div>
          <Footer />
        </div>
      </BrowserRouter>
    </AuthProvider>
  )
}
