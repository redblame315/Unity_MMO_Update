﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ConnectionToServer : MonoBehaviour {
	
	[SerializeField] String ip;
	[SerializeField] int port;

	[SerializeField] bool autoconnect = true;

	TcpClient tcpClient;
	Socket socket;
	int clientId = -1;

	[SerializeField] List<NetworkIdentity> spawnablePrefabs;
	[SerializeField] Dictionary<int, NetworkIdentity> netComps = new Dictionary<int, NetworkIdentity>();
	int idIndex = 10000;

	Thread msgReceiveThread;

	String sceneName;

	void Awake()
	{
		sceneName = gameObject.scene.name;

		NetworkIdentity[] ids = FindObjectsOfType<NetworkIdentity>();
		foreach (NetworkIdentity netComp in ids) {
			if (netComp.gameObject.scene != gameObject.scene)
				continue;

			netComp.gameObject.SetActive(false);

			netComp.id = idIndex++;
			netComp.spawned = false;
			netComps.Add(idIndex, netComp);
		};

		if (autoconnect)
			Connect();
	}

	void OnDestroy() {
		Disconnect();
	}

	void Connect(NetworkIdentity player = null) {
		if (socket != null && socket.Connected) {
			Disconnect();
		}

		tcpClient = new TcpClient(ip, port);

		socket = tcpClient.Client;

		if(socket.Connected)
		{
			socket.Send(BitConverter.GetBytes(0), SocketFlags.None);

			byte[] buffer = new byte[sizeof(int)];
			Read(ref buffer);
			int length = BitConverter.ToInt32(buffer, 0);

			buffer = new byte[length];
			Read(ref buffer);

			if ((MessageId) buffer[0] == MessageId.Spawn) {
				
				SpawnMessage msg = new SpawnMessage();
				msg.Deserialize(ref buffer);
				clientId = msg.objectId;

				if (player != null) {
					player.connectionToServer.Leave(player);						

					SceneManager.MoveGameObjectToScene(player.gameObject, gameObject.scene);
					player.id = msg.objectId;
					player.spawned = true;

					player.connectionToServer = this;

					netComps.Add(msg.objectId, player);
				} else {
					messageQueue.Enqueue(buffer);
				}
			} else {
				Debug.LogError("[" + sceneName + "] Failed to get client data");
				Disconnect();
				return;
			}

			Debug.Log("[" + sceneName + "] Client connected to " + ip + ":" + port + " with id: " + clientId + "!");
		}

		if (!socket.Connected) {
			Debug.LogWarning("[" + sceneName + "] Failed to connect " + ip + ":" + port + "!");
			gameObject.SetActive(false);
		} else {
			foreach ( KeyValuePair<int, NetworkIdentity> netComp in netComps) {
				GameObject go = netComp.Value.gameObject;
				go.SendMessage("OnConnect", this, SendMessageOptions.DontRequireReceiver);
				go.SetActive(true);
			}

			StartCoroutine(MsgHandling());
			
			msgReceiveThread = new Thread(new ThreadStart(MsgThread));
			msgReceiveThread.Start();
		}
	}

	void Disconnect() {
		foreach ( KeyValuePair<int, NetworkIdentity> netComp in netComps) {
            if (netComp.Value != null)
            {
                netComp.Value.SendMessage("OnDisconnect", null, SendMessageOptions.DontRequireReceiver);
            }
		}
		netComps.Clear();

		if (tcpClient == null || !tcpClient.Connected) {
			return;
		}

		if (msgReceiveThread != null)
			msgReceiveThread.Abort();
		
		DespawnMessage msg = new DespawnMessage();;
		msg.objectId = clientId;

		Send(msg);

		// tcpClient.Close();

		Debug.Log("[" + sceneName + "] Disconected");
	}

	void OnTriggerEnter(Collider other)
    {
		if (other.tag != "Player" || other.gameObject.scene == gameObject.scene)
			return;

        Debug.Log("[" + sceneName + "] Entered by " + other.name);

		NetworkIdentity netId = other.GetComponent<NetworkIdentity>();

		if (netId.hasAuthority)
			Connect(netId);
    }

	void Leave(NetworkIdentity player) {
		Debug.Log("[" + sceneName + "] Left by " + player.id);
		
		netComps.Remove(player.id);
		Disconnect();
	}

	// Message handling
	Queue<byte[]> messageQueue = new Queue<byte[]>();
	IEnumerator MsgHandling() {
		while(true) {
			if (messageQueue.Count <= 0) {
				yield return null;
				continue;
			}

			byte[] buffer = messageQueue.Dequeue();
			
			if ((MessageId) buffer[0] == MessageId.Transform) {
				TransformMessage msg = new TransformMessage();
				msg.Deserialize(ref buffer);

				if (!netComps.ContainsKey(msg.sourceId)) {
					Debug.LogWarning("[" + sceneName + "] Object with netId " + msg.sourceId + " not found!");
					continue;
				}

				NetworkIdentity netId = netComps[msg.sourceId];

				netId.SendMessage("ApplyTransform", msg, SendMessageOptions.DontRequireReceiver);

			} else if ((MessageId) buffer[0] ==  MessageId.Spawn) {
				SpawnMessage msg = new SpawnMessage();
				msg.Deserialize(ref buffer);

				if (netComps.ContainsKey(msg.objectId)) {
					continue;
				}

				GameObject spawned = Instantiate(spawnablePrefabs[msg.prefabId].gameObject, transform.position, transform.rotation);
				SceneManager.MoveGameObjectToScene(spawned, gameObject.scene);
				spawned.SetActive(msg.hasAuthority);

				NetworkIdentity netId = spawned.GetComponent<NetworkIdentity>();
				netComps.Add(msg.objectId, netId);

				netId.id = msg.objectId;
				netId.spawned = true;
				netId.connectionToServer = this;
				netId.hasAuthority = msg.hasAuthority;
			} else if ((MessageId) buffer[0] ==  MessageId.Despawn) {
				DespawnMessage msg = new DespawnMessage();
				msg.Deserialize(ref buffer);

				if (netComps.ContainsKey(msg.objectId)) {
					NetworkIdentity netComp = netComps[msg.objectId];
					netComps.Remove(msg.objectId);

					Destroy(netComp.gameObject);
				}
			} else if ((MessageId) buffer[0] ==  MessageId.Enable) {
				EnableMessage msg = new EnableMessage();
				msg.Deserialize(ref buffer);

				if (netComps.ContainsKey(msg.objectId)) {
					NetworkIdentity netComp = netComps[msg.objectId];

					netComp.transform.position = new Vector3(msg.position[0], msg.position[1], msg.position[2]);
					netComp.transform.rotation = new Quaternion(msg.rotation[0], msg.rotation[1], msg.rotation[2], msg.rotation[3]);									

					netComp.gameObject.SetActive(msg.toEnable);
				}
			}

			yield return null;
		}
	}

	// Message reception
	void MsgThread()
	{
		while (true) {
			byte[] buffer = new byte[sizeof(int)];
			Read(ref buffer);
			int length = BitConverter.ToInt32(buffer, 0);

			if (length <= 0) {
				continue;
			}		

			buffer = new byte[length];
			Read(ref buffer);		

			// Debug.Log("[" + sceneName + "] Received message id: " + (MessageId) buffer[0]);

			messageQueue.Enqueue(buffer);
		}
	}

	public void Send(NetworkMessage msg) {		
		byte[] buffer;

		msg.Serialize(out buffer);

		Write(ref buffer);
	}


	// Socket methods to send buffers
	int Read(ref byte[] bytes, SocketFlags flags = SocketFlags.None) {
		if (socket == null || !socket.Connected)
			return 0;
		
		return socket.Receive(bytes, flags);
	}

	int Write(ref byte[] bytes, SocketFlags flags = SocketFlags.None) {
		if (socket == null || !socket.Connected)
			return 0;
		
		return socket.Send(bytes, flags);
	}
}
