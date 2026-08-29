import { useEffect, useState } from 'react'
import { codeburn } from '../lib/ipc'
import { showToast } from '../lib/toast'
import { PluginDetailsModal } from './PluginDetails'
import { InstallFlowModal } from './InstallFlow'
import styles from './Plugins.module.css'

interface PluginInfo {
  name: string
  version: string
  status: 'loaded' | 'rejected'
  reason?: string
  capabilities?: {
    commands: string[]
    syncAttributes: Array<{ key: string; disclosure: string }>
    payloadSections: string[]
    spanKinds: string[]
  }
}

export function PluginsSection() {
  const [plugins, setPlugins] = useState<PluginInfo[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [detailsPlugin, setDetailsPlugin] = useState<string | null>(null)
  const [showInstallFlow, setShowInstallFlow] = useState(false)
  const [removing, setRemoving] = useState<string | null>(null)
  const [confirming, setConfirming] = useState<string | null>(null)

  useEffect(() => {
    void loadPlugins()
  }, [])

  async function loadPlugins() {
    try {
      setLoading(true)
      const result = await codeburn.pluginList()
      setPlugins(result as PluginInfo[])
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      setPlugins([])
    } finally {
      setLoading(false)
    }
  }

  async function verifyPlugin(name: string) {
    try {
      const result = await codeburn.pluginVerify(name)
      showToast(result.ok ? 'Plugin verified' : (result.stderr || 'Verification failed'), result.ok ? 'ok' : 'error')
      if (result.ok) void loadPlugins()
    } catch (err) {
      showToast(err instanceof Error ? err.message : String(err), 'error')
    }
  }

  async function removePlugin(name: string) {
    setRemoving(name)
    try {
      const result = await codeburn.pluginRemove(name)
      showToast(result.ok ? `Removed ${name}` : (result.stderr || 'Removal failed'), result.ok ? 'ok' : 'error')
      if (result.ok) {
        setConfirming(null)
        void loadPlugins()
      }
    } catch (err) {
      showToast(err instanceof Error ? err.message : String(err), 'error')
    } finally {
      setRemoving(null)
    }
  }

  if (loading) {
    return <div className={styles.container}>Loading plugins...</div>
  }

  return (
    <div className={styles.container}>
      <h1>Plugins</h1>
      {error && <div className={styles.error}>{error}</div>}
      {plugins.length === 0 ? (
        <div className={styles.empty}>
          <p>No plugins installed</p>
          <button className="btnp" onClick={() => void loadPlugins()}>Refresh</button>
          <button className="btnp btnp-primary" onClick={() => setShowInstallFlow(true)} style={{ marginLeft: '0.5rem' }}>Install plugin</button>
        </div>
      ) : (
        <div className={styles.list}>
          {plugins.map(plugin => (
            <div key={plugin.name} className={styles.row} data-status={plugin.status}>
              <div className={styles.info}>
                <div className={styles.name}>{plugin.name}@{plugin.version}</div>
                {plugin.status === 'rejected' && (
                  <div className={styles.reason}>{plugin.reason}</div>
                )}
                {plugin.capabilities && (
                  <div className={styles.caps}>
                    {plugin.capabilities.commands.length > 0 && (
                      <span>commands {plugin.capabilities.commands.length}</span>
                    )}
                    {plugin.capabilities.syncAttributes.length > 0 && (
                      <span>fields {plugin.capabilities.syncAttributes.length}</span>
                    )}
                    {plugin.capabilities.payloadSections.length > 0 && (
                      <span>sections {plugin.capabilities.payloadSections.length}</span>
                    )}
                  </div>
                )}
              </div>
              {plugin.status === 'loaded' && (
                <div className={styles.actions}>
                  <button className="btnp" onClick={() => setDetailsPlugin(plugin.name)} title="View plugin details">
                    Details
                  </button>
                  <button className="btnp" onClick={() => void verifyPlugin(plugin.name)} title="Verify plugin signature">
                    Verify
                  </button>
                  {confirming === plugin.name ? (
                    <span style={{ display: 'flex', gap: '0.25rem', alignItems: 'center' }}>
                      <span style={{ fontSize: '0.875rem', color: 'var(--mut)' }}>Remove {plugin.name}?</span>
                      <button className="btnp" onClick={() => void removePlugin(plugin.name)} disabled={removing === plugin.name} style={{ fontSize: '0.75rem' }}>
                        {removing === plugin.name ? 'Removing...' : 'Yes'}
                      </button>
                      <button className="btnp" onClick={() => setConfirming(null)} style={{ fontSize: '0.75rem' }}>
                        No
                      </button>
                    </span>
                  ) : (
                    <button className="btnp" onClick={() => setConfirming(plugin.name)} title="Remove plugin">
                      Remove
                    </button>
                  )}
                </div>
              )}
            </div>
          ))}
        </div>
      )}
      <button className="btnp btnp-primary" onClick={() => setShowInstallFlow(true)} style={{ marginTop: '1.5rem' }}>
        Install plugin
      </button>

      {detailsPlugin && (
        <PluginDetailsModal
          pluginName={detailsPlugin}
          onClose={() => setDetailsPlugin(null)}
        />
      )}

      {showInstallFlow && (
        <InstallFlowModal
          onClose={() => setShowInstallFlow(false)}
          onSuccess={() => {
            void loadPlugins()
            setShowInstallFlow(false)
          }}
        />
      )}
    </div>
  )
}
