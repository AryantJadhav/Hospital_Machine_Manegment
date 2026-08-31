import { useCallback, useEffect, useState } from 'react';
import {
  ActivityIndicator,
  FlatList,
  RefreshControl,
  StyleSheet,
  Text,
  TouchableOpacity,
  View,
} from 'react-native';
import { api, ApiError, type PmTask } from '../api';
import { theme } from '../theme';

/**
 * What is due, without having to find the machines first.
 *
 * The scanner answers "what is this machine"; this answers "what should I do
 * today". A technician starting a shift should not have to walk a ward
 * scanning tags to discover there was nothing due on any of them.
 */
export function WorkListScreen({
  onOpenTask,
  onScan,
  onSignOut,
}: {
  onOpenTask: (taskId: number) => void;
  onScan: () => void;
  onSignOut: () => void;
}) {
  const [tasks, setTasks] = useState<PmTask[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (isRefresh = false) => {
    if (isRefresh) setRefreshing(true);
    else setLoading(true);
    setError(null);

    try {
      // Overdue first, then due. The server already orders by due date, so
      // the oldest miss is at the top where it belongs.
      const data = await api.openTasks();
      setTasks(data.items);
    } catch (e) {
      setError(
        e instanceof ApiError && e.status === 0
          ? 'No signal. Pull down to try again when you are back in range.'
          : e instanceof Error
            ? e.message
            : 'Could not load your work.',
      );
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  if (loading) {
    return (
      <View style={styles.centre}>
        <ActivityIndicator color={theme.accent} size="large" />
      </View>
    );
  }

  return (
    <View style={styles.container}>
      <View style={styles.head}>
        <View style={{ flex: 1 }}>
          <Text style={styles.title}>Due now</Text>
          <Text style={styles.sub}>
            {tasks.length === 0 ? 'Nothing outstanding' : `${tasks.length} to do`}
          </Text>
        </View>

        <TouchableOpacity style={styles.scanButton} onPress={onScan}>
          <Text style={styles.scanText}>Scan</Text>
        </TouchableOpacity>
      </View>

      {error ? <Text style={styles.error}>{error}</Text> : null}

      <FlatList
        data={tasks}
        keyExtractor={(t) => String(t.id)}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => void load(true)}
            tintColor={theme.muted}
          />
        }
        contentContainerStyle={tasks.length === 0 ? styles.emptyWrap : styles.list}
        ListEmptyComponent={
          <Text style={styles.empty}>
            No PMs are due. Pull down to refresh, or scan a machine to look it up.
          </Text>
        }
        renderItem={({ item }) => (
          <TouchableOpacity style={styles.card} onPress={() => onOpenTask(item.id)}>
            <View style={{ flex: 1 }}>
              <Text style={styles.tag}>{item.assetTag}</Text>
              <Text style={styles.checklist}>{item.checklistName}</Text>
              <Text style={styles.due}>
                Due {item.dueDate}
                {item.daysLate > 0 ? ` · ${item.daysLate} days late` : ''}
              </Text>
            </View>

            <View
              style={[
                styles.badge,
                item.status === 30 ? styles.badgeOverdue : styles.badgeDue,
              ]}
            >
              <Text
                style={[
                  styles.badgeText,
                  item.status === 30 ? styles.badgeTextOverdue : styles.badgeTextDue,
                ]}
              >
                {item.status === 30 ? 'Overdue' : item.status === 20 ? 'Due' : 'Scheduled'}
              </Text>
            </View>
          </TouchableOpacity>
        )}
      />

      <TouchableOpacity style={styles.signOut} onPress={onSignOut}>
        <Text style={styles.signOutText}>Sign out</Text>
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: theme.bg },
  centre: { flex: 1, justifyContent: 'center', alignItems: 'center', backgroundColor: theme.bg },
  head: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 20,
    paddingTop: 14,
    paddingBottom: 10,
    gap: 12,
  },
  title: { fontSize: 24, fontWeight: '700', color: theme.text },
  sub: { fontSize: 14, color: theme.muted, marginTop: 2 },
  scanButton: {
    backgroundColor: theme.accent,
    paddingHorizontal: 18,
    paddingVertical: 12,
    borderRadius: 8,
  },
  scanText: { color: '#fff', fontSize: 15, fontWeight: '600' },
  error: { color: theme.danger, paddingHorizontal: 20, paddingBottom: 8, fontSize: 14 },
  list: { padding: 14, paddingTop: 4, gap: 10 },
  emptyWrap: { flexGrow: 1, justifyContent: 'center', padding: 32 },
  empty: { color: theme.muted, textAlign: 'center', fontSize: 15, lineHeight: 21 },
  card: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    backgroundColor: theme.surface,
    borderWidth: 1,
    borderColor: theme.border,
    borderRadius: 10,
    // Generous target: this is tapped one-handed, in gloves.
    padding: 16,
  },
  tag: { fontSize: 17, fontWeight: '600', color: theme.text },
  checklist: { fontSize: 14, color: theme.muted, marginTop: 2 },
  due: { fontSize: 13, color: theme.muted, marginTop: 4 },
  badge: { paddingHorizontal: 10, paddingVertical: 6, borderRadius: 999 },
  badgeDue: { backgroundColor: '#33280f' },
  badgeOverdue: { backgroundColor: '#3a1a1c' },
  badgeText: { fontSize: 12, fontWeight: '600' },
  badgeTextDue: { color: '#e0b071' },
  badgeTextOverdue: { color: theme.danger },
  signOut: { alignSelf: 'center', padding: 16 },
  signOutText: { color: theme.muted, fontSize: 14 },
});
