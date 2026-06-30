import { useState, useEffect } from 'react';
import { StyleSheet, Text, View, TextInput, Button, Image, ActivityIndicator, Alert, FlatList, TouchableOpacity, Modal, ScrollView } from 'react-native';
import * as FileSystem from 'expo-file-system/legacy';
import * as MediaLibrary from 'expo-media-library';
import * as Clipboard from 'expo-clipboard';
import AsyncStorage from '@react-native-async-storage/async-storage';

const API_BASE_URL = 'https://issams-tube-backend-production.up.railway.app';
const API_KEY = 'my-super-secret-key-2017';

export default function App() {
  // ── Video screen state ────────────────────────────────────────
  const [url, setUrl] = useState('');
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState(null);
  const [downloading, setDownloading] = useState(false);
  const [downloadProgress, setDownloadProgress] = useState(0);
  const [preparing, setPreparing] = useState(false);

  // ── Audio screen state ────────────────────────────────────────
  const [audioUrl, setAudioUrl] = useState('');
  const [audioExtracting, setAudioExtracting] = useState(false);
  const [audioDownloading, setAudioDownloading] = useState(false);
  const [audioProgress, setAudioProgress] = useState(0);

  // ── Navigation & History & Downloads ─────────────────────────
  const [screen, setScreen] = useState('home'); // 'home' | 'audio' | 'instagram' | 'history' | 'downloads'
  const [history, setHistory] = useState([]);
  
  const [downloads, setDownloads] = useState([]);
  const [downloadTab, setDownloadTab] = useState('video'); // 'video' | 'audio'
  const [selectedDownloadInfo, setSelectedDownloadInfo] = useState(null);

  useEffect(() => {
    loadLocalDownloads();
  }, []);

  const loadLocalDownloads = async () => {
    try {
      const stored = await AsyncStorage.getItem('@issamstube_downloads');
      if (stored) setDownloads(JSON.parse(stored));
    } catch (e) {
      console.log('Failed to load downloads', e);
    }
  };

  const saveDownloadToStorage = async (item) => {
    try {
      const updated = [item, ...downloads];
      setDownloads(updated);
      await AsyncStorage.setItem('@issamstube_downloads', JSON.stringify(updated));
    } catch (e) {
      console.log('Failed to save download record', e);
    }
  };

  const loadHistory = async () => {
    try {
      const response = await fetch(`${API_BASE_URL}/history`, {
        headers: API_KEY ? { 'X-Api-Key': API_KEY } : {},
      });
      const data = await response.json();
      setHistory(data);
    } catch (err) {
      Alert.alert('Could not load history', err.message);
    }
  };

  useEffect(() => {
    if (screen === 'history') loadHistory();
  }, [screen]);

  // ── Video handlers ────────────────────────────────────────────
  const handleFetch = async () => {
    setLoading(true);
    setResult(null);
    try {
      const response = await fetch(`${API_BASE_URL}/extract`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(API_KEY ? { 'X-Api-Key': API_KEY } : {}),
        },
        body: JSON.stringify({ url }),
      });
      const data = await response.json();
      if (!response.ok) {
        Alert.alert('Error', data.message || 'Something went wrong');
        return;
      }
      setResult(data);
    } catch (err) {
      Alert.alert('Network error', err.message);
    } finally {
      setLoading(false);
    }
  };

  const handleDownload = async () => {
    if (!url) return;

    const { status } = await MediaLibrary.requestPermissionsAsync();
    if (status !== 'granted') {
      Alert.alert('Permission needed', 'Allow access to save videos to your gallery.');
      return;
    }

    setPreparing(true);
    setDownloadProgress(0);

    try {
      const prepResponse = await fetch(`${API_BASE_URL}/download`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(API_KEY ? { 'X-Api-Key': API_KEY } : {}),
        },
        body: JSON.stringify({ url }),
      });
      const prepData = await prepResponse.json();
      if (!prepResponse.ok) {
        Alert.alert('Error', prepData.message || 'Could not prepare the download');
        return;
      }

      setPreparing(false);
      setDownloading(true);

      const downloadHeaders = API_KEY ? { 'X-Api-Key': API_KEY } : {};
      
      const urlsToDownload = (prepData.download_urls && prepData.download_urls.length > 0)
        ? prepData.download_urls
        : [prepData.download_url];

      for (let i = 0; i < urlsToDownload.length; i++) {
        const currentUrl = urlsToDownload[i];
        if (!currentUrl) continue;
        
        const sourceUrl = currentUrl.startsWith('http')
          ? currentUrl
          : `${API_BASE_URL}${currentUrl}`;

        const extension = prepData.media_type === 'image' ? 'jpg' : 'mp4';
        const finalTitle = prepData.title || result?.title || `IssamsTube_Media_${Date.now()}`;
        const titleSafe = finalTitle.replace(/[^a-zA-Z0-9]/g, '_');
        
        // Save using the safe title
        const fileUri = FileSystem.documentDirectory + `${titleSafe}_${i}.${extension}`;

        const downloadResumable = FileSystem.createDownloadResumable(
          sourceUrl,
          fileUri,
          { headers: downloadHeaders },
          (progress) => {
            const filePct = progress.totalBytesWritten / progress.totalBytesExpectedToWrite;
            const overallPct = (i + filePct) / urlsToDownload.length;
            setDownloadProgress(overallPct);
          }
        );

        const downloadResult = await downloadResumable.downloadAsync();
        const asset = await MediaLibrary.createAssetAsync(downloadResult.uri);
        await MediaLibrary.createAlbumAsync("Issam's Tube", asset, false);

        try {
          const fileInfo = await FileSystem.getInfoAsync(downloadResult.uri);
          await saveDownloadToStorage({
            id: Date.now().toString() + i,
            title: finalTitle,
            type: 'video', // Covers video/images
            sizeBytes: fileInfo.size,
            date: new Date().toISOString(),
            sourceUrl: url
          });
        } catch (e) {
          console.log('Error saving metadata', e);
        }
      }

      setDownloadProgress(1.0);
      Alert.alert('Saved', 'Saved to your gallery in the best quality available.');
    } catch (err) {
      Alert.alert('Download failed', err.message);
    } finally {
      setPreparing(false);
      setDownloading(false);
    }
  };

  const handlePasteFromClipboard = async () => {
    const text = await Clipboard.getStringAsync();
    if (text) setUrl(text);
  };

  // ── Audio handler ─────────────────────────────────────────────
  const handleAudioExtract = async () => {
    if (!audioUrl) return;

    const { status } = await MediaLibrary.requestPermissionsAsync();
    if (status !== 'granted') {
      Alert.alert('Permission needed', 'Allow access to save audio to your device.');
      return;
    }

    setAudioExtracting(true);
    setAudioProgress(0);

    try {
      const response = await fetch(`${API_BASE_URL}/audio`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(API_KEY ? { 'X-Api-Key': API_KEY } : {}),
        },
        body: JSON.stringify({ url: audioUrl }),
      });
      const data = await response.json();
      if (!response.ok) {
        Alert.alert('Error', data.message || 'Could not extract audio.');
        return;
      }

      setAudioExtracting(false);
      setAudioDownloading(true);

      const sourceUrl = data.download_url.startsWith('http')
        ? data.download_url
        : `${API_BASE_URL}${data.download_url}`;

      const finalTitle = data.title || `IssamsTube_Audio_${Date.now()}`;
      const titleSafe = finalTitle.replace(/[^a-zA-Z0-9]/g, '_');
      const fileUri = FileSystem.documentDirectory + `${titleSafe}.mp3`;

      const downloadResumable = FileSystem.createDownloadResumable(
        sourceUrl,
        fileUri,
        { headers: API_KEY ? { 'X-Api-Key': API_KEY } : {} },
        (progress) => {
          const pct = progress.totalBytesWritten / progress.totalBytesExpectedToWrite;
          setAudioProgress(pct);
        }
      );

      const downloadResult = await downloadResumable.downloadAsync();
      await MediaLibrary.saveToLibraryAsync(downloadResult.uri);

      try {
        const fileInfo = await FileSystem.getInfoAsync(downloadResult.uri);
        await saveDownloadToStorage({
          id: Date.now().toString(),
          title: finalTitle,
          type: 'audio',
          sizeBytes: fileInfo.size,
          date: new Date().toISOString(),
          sourceUrl: audioUrl
        });
      } catch (e) {
        console.log('Error saving audio metadata', e);
      }

      Alert.alert('Saved', 'MP3 (HD quality) saved to your device.');
    } catch (err) {
      Alert.alert('Extraction failed', err.message);
    } finally {
      setAudioExtracting(false);
      setAudioDownloading(false);
    }
  };

  const handleAudioPaste = async () => {
    const text = await Clipboard.getStringAsync();
    if (text) setAudioUrl(text);
  };

  // Helper for size formatting
  const formatBytes = (bytes, decimals = 2) => {
    if (!+bytes) return '0 Bytes';
    const k = 1024;
    const dm = decimals < 0 ? 0 : decimals;
    const sizes = ['Bytes', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return `${parseFloat((bytes / Math.pow(k, i)).toFixed(dm))} ${sizes[i]}`;
  };

  // ── Render ────────────────────────────────────────────────────
  return (
    <View style={styles.container}>
      {/* Header */}
      <Text style={styles.title}>Issam's Tube</Text>

      <ScrollView contentContainerStyle={{ paddingBottom: 100 }}>
        {/* ── Video screen ── */}
        {screen === 'home' && (
          <View style={styles.screenWrapper}>
            <View style={{ marginBottom: 12 }}>
              <Button title="Paste from clipboard" onPress={handlePasteFromClipboard} color="#666" />
            </View>

            <TextInput
              style={styles.input}
              placeholder="Paste video URL here"
              value={url}
              onChangeText={setUrl}
              autoCapitalize="none"
              autoCorrect={false}
            />

            <Button title="Fetch Video" onPress={handleFetch} disabled={!url || loading} />

            {loading && <ActivityIndicator size="large" style={{ marginTop: 20 }} />}

            {result && (
              <View style={styles.resultCard}>
                {result.thumbnail && <Image source={{ uri: result.thumbnail }} style={styles.thumbnail} />}
                <Text style={styles.resultTitle}>{result.title}</Text>
                <Button
                  title={
                    preparing
                      ? (result.media_type === 'image' ? 'Preparing photo…' : 'Preparing high-quality video…')
                      : downloading
                        ? `Downloading… ${Math.round(downloadProgress * 100)}%`
                        : (result.media_type === 'image' ? 'Save Photo' : 'Save Video')
                  }
                  color="#2563eb"
                  onPress={handleDownload}
                  disabled={preparing || downloading}
                />
              </View>
            )}
          </View>
        )}

        {/* ── Audio screen ── */}
        {screen === 'audio' && (
          <View style={styles.screenWrapper}>
            <View style={styles.audioHeader}>
              <Text style={styles.audioSubtitle}>Extract HD audio from any video link</Text>
              <Text style={styles.audioNote}>YouTube · Instagram · TikTok · X · Facebook</Text>
            </View>

            <View style={{ marginBottom: 12 }}>
              <Button title="Paste from clipboard" onPress={handleAudioPaste} color="#666" />
            </View>

            <TextInput
              style={styles.input}
              placeholder="Paste video URL here"
              value={audioUrl}
              onChangeText={setAudioUrl}
              autoCapitalize="none"
              autoCorrect={false}
            />

            <View style={styles.extractBtn}>
              <Button
                title={
                  audioExtracting
                    ? 'Extracting audio… (this may take a minute)'
                    : audioDownloading
                      ? `Downloading MP3… ${Math.round(audioProgress * 100)}%`
                      : '🎵 Extract MP3 HD'
                }
                onPress={handleAudioExtract}
                disabled={!audioUrl || audioExtracting || audioDownloading}
                color="#7c3aed"
              />
            </View>

            {(audioExtracting || audioDownloading) && (
              <View style={styles.audioProgress}>
                <ActivityIndicator size="large" color="#7c3aed" />
                <Text style={styles.audioProgressText}>
                  {audioExtracting
                    ? 'Server is downloading and converting to MP3…'
                    : `Saving to your device… ${Math.round(audioProgress * 100)}%`}
                </Text>
              </View>
            )}
          </View>
        )}

        {/* ── Instagram Pictures screen ── */}
        {screen === 'instagram' && (
          <View style={styles.screenWrapper}>
            <View style={styles.audioHeader}>
              <Text style={styles.audioSubtitle}>Download high-quality Instagram Photos</Text>
              <Text style={styles.audioNote}>Paste any Instagram post or reel link</Text>
            </View>

            <View style={{ marginBottom: 12 }}>
              <Button title="Paste from clipboard" onPress={handlePasteFromClipboard} color="#666" />
            </View>

            <TextInput
              style={styles.input}
              placeholder="Paste Instagram URL here"
              value={url}
              onChangeText={setUrl}
              autoCapitalize="none"
              autoCorrect={false}
            />

            <View style={styles.extractBtn}>
              <Button
                title="Fetch Instagram Picture"
                onPress={handleFetch}
                disabled={!url || loading}
                color="#e1306c"
              />
            </View>

            {loading && <ActivityIndicator size="large" color="#e1306c" style={{ marginTop: 20 }} />}

            {result && (
              <View style={styles.resultCard}>
                {result.thumbnail && <Image source={{ uri: result.thumbnail }} style={styles.thumbnail} />}
                <Text style={styles.resultTitle}>{result.title}</Text>
                <Button
                  title={
                    preparing
                      ? 'Preparing photo…'
                      : downloading
                        ? `Downloading… ${Math.round(downloadProgress * 100)}%`
                        : 'Save Photo to Gallery'
                  }
                  color="#e1306c"
                  onPress={handleDownload}
                  disabled={preparing || downloading}
                />
              </View>
            )}
          </View>
        )}

        {/* ── History screen ── */}
        {screen === 'history' && (
          <View style={styles.screenWrapper}>
            <FlatList
              data={history}
              scrollEnabled={false}
              keyExtractor={(item) => String(item.id)}
              renderItem={({ item }) => (
                <View style={styles.historyRow}>
                  <Text style={styles.historyPlatform}>{platformLabel(item.platform)}</Text>
                  <Text style={styles.historyTitle} numberOfLines={1}>{item.title ?? item.url}</Text>
                  <Text style={styles.historyStatus}>{item.success ? '✅' : '❌'}</Text>
                </View>
              )}
            />
          </View>
        )}

        {/* ── Downloads screen ── */}
        {screen === 'downloads' && (
          <View style={styles.screenWrapper}>
            
            {/* Top toggle for Video / Audio inside Downloads */}
            <View style={styles.subTabBar}>
              <TouchableOpacity
                style={[styles.subTab, downloadTab === 'video' && styles.subTabActive]}
                onPress={() => setDownloadTab('video')}
              >
                <Text style={[styles.subTabText, downloadTab === 'video' && styles.subTabTextActive]}>Videos</Text>
              </TouchableOpacity>
              <TouchableOpacity
                style={[styles.subTab, downloadTab === 'audio' && styles.subTabActive]}
                onPress={() => setDownloadTab('audio')}
              >
                <Text style={[styles.subTabText, downloadTab === 'audio' && styles.subTabTextActive]}>Audios</Text>
              </TouchableOpacity>
            </View>

            <View style={{ marginTop: 10 }}>
              {downloads.filter(d => d.type === downloadTab).length === 0 ? (
                <Text style={styles.emptyText}>No {downloadTab}s downloaded yet.</Text>
              ) : (
                downloads
                  .filter(d => d.type === downloadTab)
                  .map(item => (
                    <View key={item.id} style={styles.downloadItem}>
                      <View style={{ flex: 1, paddingRight: 10 }}>
                        <Text style={styles.downloadTitle} numberOfLines={1}>{item.title}</Text>
                        <Text style={styles.downloadSubtitle}>{new Date(item.date).toLocaleDateString()} • {formatBytes(item.sizeBytes)}</Text>
                      </View>
                      <TouchableOpacity
                        style={styles.menuDotsBtn}
                        onPress={() => setSelectedDownloadInfo(item)}
                      >
                        <Text style={styles.menuDotsTxt}>⋮</Text>
                      </TouchableOpacity>
                    </View>
                  ))
              )}
            </View>
          </View>
        )}
      </ScrollView>

      {/* ── Bottom Tab Bar ── */}
      <View style={styles.bottomTabBar}>
        <TouchableOpacity style={styles.bottomTab} onPress={() => setScreen('home')}>
          <Text style={[styles.bottomTabText, screen === 'home' && styles.bottomTabTextActive]}>📹 Vid</Text>
        </TouchableOpacity>
        <TouchableOpacity style={styles.bottomTab} onPress={() => setScreen('audio')}>
          <Text style={[styles.bottomTabText, screen === 'audio' && styles.bottomTabTextActive]}>🎵 MP3</Text>
        </TouchableOpacity>
        <TouchableOpacity style={styles.bottomTab} onPress={() => setScreen('instagram')}>
          <Text style={[styles.bottomTabText, screen === 'instagram' && styles.bottomTabTextActive]}>📷 Insta</Text>
        </TouchableOpacity>
        <TouchableOpacity style={styles.bottomTab} onPress={() => setScreen('history')}>
          <Text style={[styles.bottomTabText, screen === 'history' && styles.bottomTabTextActive]}>🕐 Hist</Text>
        </TouchableOpacity>
        <TouchableOpacity style={[styles.bottomTab, { borderLeftWidth: 1, borderLeftColor: '#ccc' }]} onPress={() => setScreen('downloads')}>
          <Text style={[styles.bottomTabText, screen === 'downloads' && styles.bottomTabTextActive, { color: '#2563eb' }]}>⬇️ DLs</Text>
        </TouchableOpacity>
      </View>

      {/* ── Modal for Download Info ── */}
      <Modal
        transparent={true}
        visible={!!selectedDownloadInfo}
        animationType="fade"
        onRequestClose={() => setSelectedDownloadInfo(null)}
      >
        <View style={styles.modalOverlay}>
          <View style={styles.modalContent}>
            <Text style={styles.modalTitle}>File Info</Text>
            
            {selectedDownloadInfo && (
              <>
                <Text style={styles.modalLabel}>Name:</Text>
                <Text style={styles.modalValue}>{selectedDownloadInfo.title}</Text>
                
                <Text style={styles.modalLabel}>Size:</Text>
                <Text style={styles.modalValue}>{formatBytes(selectedDownloadInfo.sizeBytes)}</Text>
                
                <Text style={styles.modalLabel}>Date:</Text>
                <Text style={styles.modalValue}>{new Date(selectedDownloadInfo.date).toLocaleString()}</Text>
                
                <Text style={styles.modalLabel}>Source Link:</Text>
                <Text style={styles.modalValue} selectable={true}>{selectedDownloadInfo.sourceUrl}</Text>
              </>
            )}

            <TouchableOpacity style={styles.modalCloseBtn} onPress={() => setSelectedDownloadInfo(null)}>
              <Text style={styles.modalCloseTxt}>Close</Text>
            </TouchableOpacity>
          </View>
        </View>
      </Modal>
    </View>
  );
}

function platformLabel(platform) {
  switch (platform) {
    case 'TikTok': return '🎵 TikTok';
    case 'Instagram': return '📷 Instagram';
    case 'Facebook': return '👍 Facebook';
    case 'X/Twitter': return '✖️ X';
    case 'YouTube': return '▶️ YouTube';
    case 'LinkedIn': return '💼 LinkedIn';
    default: return '❓ Unknown';
  }
}

const styles = StyleSheet.create({
  container: { flex: 1, paddingTop: 50, backgroundColor: '#fff' },
  title: { fontSize: 24, fontWeight: 'bold', marginBottom: 16, textAlign: 'center' },
  screenWrapper: { paddingHorizontal: 20 },

  // Shared
  input: { borderWidth: 1, borderColor: '#ccc', borderRadius: 8, padding: 12, marginBottom: 12 },

  // Video screen
  resultCard: { marginTop: 24, alignItems: 'center' },
  thumbnail: { width: 200, height: 200, borderRadius: 8, marginBottom: 12 },
  resultTitle: { fontSize: 16, marginBottom: 12, textAlign: 'center' },

  // Audio screen
  audioHeader: { marginBottom: 20, alignItems: 'center' },
  audioSubtitle: { fontSize: 15, fontWeight: '600', color: '#374151', marginBottom: 4 },
  audioNote: { fontSize: 12, color: '#9ca3af' },
  extractBtn: { marginBottom: 8 },
  audioProgress: { marginTop: 24, alignItems: 'center', gap: 12 },
  audioProgressText: { fontSize: 13, color: '#7c3aed', textAlign: 'center' },

  // History
  historyRow: { flexDirection: 'row', alignItems: 'center', paddingVertical: 10, borderBottomWidth: 1, borderBottomColor: '#eee' },
  historyPlatform: { width: 110, fontSize: 13 },
  historyTitle: { flex: 1, fontSize: 13 },
  historyStatus: { width: 24, textAlign: 'right' },

  // Downloads Screen
  subTabBar: { flexDirection: 'row', borderRadius: 8, overflow: 'hidden', borderWidth: 1, borderColor: '#ddd', marginBottom: 10 },
  subTab: { flex: 1, paddingVertical: 10, alignItems: 'center', backgroundColor: '#f9fafb' },
  subTabActive: { backgroundColor: '#2563eb' },
  subTabText: { fontSize: 14, fontWeight: '600', color: '#666' },
  subTabTextActive: { color: '#fff' },
  emptyText: { textAlign: 'center', color: '#999', marginTop: 20 },
  downloadItem: { flexDirection: 'row', alignItems: 'center', paddingVertical: 12, borderBottomWidth: 1, borderBottomColor: '#eee' },
  downloadTitle: { fontSize: 15, fontWeight: '600', color: '#333', marginBottom: 4 },
  downloadSubtitle: { fontSize: 12, color: '#666' },
  menuDotsBtn: { padding: 10 },
  menuDotsTxt: { fontSize: 24, fontWeight: 'bold', color: '#666', lineHeight: 24 },

  // Bottom Tab Bar
  bottomTabBar: { 
    position: 'absolute', bottom: 0, left: 0, right: 0, 
    height: 60, flexDirection: 'row', 
    backgroundColor: '#fff', borderTopWidth: 1, borderTopColor: '#e5e7eb',
    elevation: 10, shadowColor: '#000', shadowOffset: { width: 0, height: -2 }, shadowOpacity: 0.1, shadowRadius: 3
  },
  bottomTab: { flex: 1, justifyContent: 'center', alignItems: 'center' },
  bottomTabText: { fontSize: 12, fontWeight: '600', color: '#6b7280' },
  bottomTabTextActive: { color: '#000' },

  // Modal
  modalOverlay: { flex: 1, backgroundColor: 'rgba(0,0,0,0.5)', justifyContent: 'center', alignItems: 'center', padding: 20 },
  modalContent: { width: '100%', backgroundColor: '#fff', borderRadius: 12, padding: 20, elevation: 5 },
  modalTitle: { fontSize: 18, fontWeight: 'bold', marginBottom: 15, textAlign: 'center' },
  modalLabel: { fontSize: 12, color: '#666', marginTop: 10 },
  modalValue: { fontSize: 15, color: '#000', fontWeight: '500' },
  modalCloseBtn: { marginTop: 20, backgroundColor: '#f3f4f6', paddingVertical: 12, borderRadius: 8, alignItems: 'center' },
  modalCloseTxt: { fontSize: 16, fontWeight: '600', color: '#374151' }
});
