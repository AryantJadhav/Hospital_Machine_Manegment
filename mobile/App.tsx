import { useCallback, useEffect, useState } from 'react';
import { ActivityIndicator, StyleSheet, View } from 'react-native';
import { StatusBar } from 'expo-status-bar';
import { SafeAreaView } from 'react-native';
import { api, clearSession, setServerUrl, type Equipment, type PmForm, type PmTask } from './src/api';
import { queue } from './src/queue';
import { storage } from './src/storage';
import { theme } from './src/theme';
import { ServerSetupScreen } from './src/screens/ServerSetupScreen';
import { LoginScreen } from './src/screens/LoginScreen';
import { ScanScreen } from './src/screens/ScanScreen';
import { EquipmentDetailScreen } from './src/screens/EquipmentDetailScreen';
import { PmChecklistScreen } from './src/screens/PmChecklistScreen';

type Stage = 'loading' | 'server' | 'login' | 'scan' | 'detail' | 'checklist';

/**
 * Four screens, switched by state rather than a navigation library.
 *
 * The flow is strictly linear — server, then sign in, then scan, then the
 * asset — with no deep links or tabs. React Navigation would add a
 * substantial dependency for a back button this app does not have.
 * Revisit when the PM execution screens arrive in week 11.
 */
export default function App() {
  const [stage, setStage] = useState<Stage>('loading');
  const [server, setServer] = useState<string | null>(null);
  const [equipment, setEquipment] = useState<Equipment | null>(null);
  const [pmTasks, setPmTasks] = useState<PmTask[]>([]);
  const [form, setForm] = useState<PmForm | null>(null);

  useEffect(() => {
    let cancelled = false;

    (async () => {
      const url = await storage.getServerUrl();

      if (!url) {
        if (!cancelled) setStage('server');
        return;
      }

      setServerUrl(url);
      if (!cancelled) setServer(url);

      // Try the stored refresh token before showing a login form: a
      // technician reopening the app between wards should land on the
      // scanner, not a password prompt.
      const restored = await api.restoreSession();

      // Anything queued while offline goes out as soon as there is a session
      // again, without the technician having to remember.
      if (restored) void queue.flush();

      if (!cancelled) setStage(restored ? 'scan' : 'login');
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  const signOut = useCallback(async () => {
    await clearSession();
    setEquipment(null);
    setStage('login');
  }, []);

  const changeServer = useCallback(async () => {
    await clearSession();
    await storage.setServerUrl(null);
    setServerUrl(null);
    setServer(null);
    setStage('server');
  }, []);

  return (
    <SafeAreaView style={styles.root}>
      <StatusBar style="light" />

      {stage === 'loading' && (
        <View style={styles.centre}>
          <ActivityIndicator color={theme.accent} size="large" />
        </View>
      )}

      {stage === 'server' && (
        <ServerSetupScreen
          onDone={(url) => {
            setServer(url);
            setStage('login');
          }}
        />
      )}

      {stage === 'login' && server && (
        <LoginScreen
          serverUrl={server}
          onSignedIn={() => setStage('scan')}
          onChangeServer={() => void changeServer()}
        />
      )}

      {stage === 'scan' && (
        <ScanScreen
          onFound={async (found) => {
            setEquipment(found);
            // Fetched alongside the machine so the technician sees what is
            // due without a second deliberate step.
            try {
              const tasks = await api.tasksForEquipment(found.id);
              setPmTasks(tasks.items);
            } catch {
              setPmTasks([]);
            }
            setStage('detail');
          }}
          onSignOut={() => void signOut()}
        />
      )}

      {stage === 'detail' && equipment && (
        <EquipmentDetailScreen
          equipment={equipment}
          pmTasks={pmTasks}
          onStartPm={async (taskId) => {
            try {
              setForm(await api.taskForm(taskId));
              setStage('checklist');
            } catch {
              // Opening a PM needs connectivity by design: the checklist
              // version has to come from the server so the completion is
              // pinned to what was actually published.
            }
          }}
          onBack={() => {
            setEquipment(null);
            setPmTasks([]);
            setStage('scan');
          }}
        />
      )}

      {stage === 'checklist' && form && (
        <PmChecklistScreen
          form={form}
          onCancel={() => {
            setForm(null);
            setStage('detail');
          }}
          onDone={() => {
            setForm(null);
            setEquipment(null);
            setPmTasks([]);
            setStage('scan');
          }}
        />
      )}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: theme.bg },
  centre: { flex: 1, justifyContent: 'center', alignItems: 'center' },
});
