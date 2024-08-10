using UnityEngine;
using System.Collections;

public class SunRotates : MonoBehaviour
{

	[Range(0f, 24f)]
	public float currentTime;   // 0 and 24 equal to 12am (midnight) , 12 euals to 12pm (noon)
	public float dayLengthInSeconds; // How long (in second) do you want a day to last?

	void Start()
	{

	}
	void Update()
	{
		float speed = 24f / dayLengthInSeconds;

		currentTime += Time.deltaTime * speed;

		if (currentTime >= 24f)
			currentTime = 0f;
		transform.rotation = Quaternion.Euler((currentTime - 6) * 15f, 0f, 0f);
	}
}

